using System.Net;
using System.Net.Http.Json;
using Konnyaku.Billing.Application;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BillingTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Paid_order_enrolls_the_student_and_lessons_are_consumed_once()
    {
        var c = await CenterAsync("bill", capacity: 5);
        var order = await CreateOrderAsync(c, c.ClassId);

        // Two partial payments: credits open only when fully paid.
        (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order.Id}/payments", new { amount = 1_000_000m, method = "cash" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.Admin.GetFromJsonAsync<CreditView[]>($"/api/billing/students/{c.StudentId}/credits", Ct))!.ShouldBeEmpty();
        var overpay = await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order.Id}/payments", new { amount = 5_000_000m, method = "cash" }, Ct);
        (await overpay.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("billing.payment_amount_invalid");
        var paid = await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order.Id}/payments",
            new { amount = 1_400_000m, method = "bank_transfer" }, Ct)).Content.ReadFromJsonAsync<OrderView>(Ct);
        paid!.Status.ShouldBe("Paid");

        await _api.DrainOutboxAsync();

        var roster = (await c.Admin.GetFromJsonAsync<RosterView[]>($"/api/academic/classes/{c.ClassId}/roster", Ct))!;
        roster.Length.ShouldBe(1);
        var account = (await c.Admin.GetFromJsonAsync<CreditView[]>($"/api/billing/students/{c.StudentId}/credits", Ct))!.Single();
        account.EnrollmentId.ShouldBe(roster[0].EnrollmentId);
        account.PaidCredits.ShouldBe(24m);
        account.BonusCredits.ShouldBe(2m);

        var attendance = Guid.NewGuid();
        var date = new DateOnly(2027, 1, 5);
        (await ConsumeAsync(c, roster[0].EnrollmentId, attendance, date)).Consumed.ShouldBeTrue();
        (await ConsumeAsync(c, roster[0].EnrollmentId, attendance, date)).Consumed.ShouldBeFalse();

        account = (await c.Admin.GetFromJsonAsync<CreditView[]>($"/api/billing/students/{c.StudentId}/credits", Ct))!.Single();
        account.PaidCredits.ShouldBe(23m);

        // 2,400,000 / 24 lessons = 100,000 recognized for the one taught lesson, on the class branch.
        var revenue = (await c.Admin.GetFromJsonAsync<RevenueView[]>("/api/billing/reports/recognized-revenue?from=2027-01-01&to=2027-01-31", Ct))!;
        revenue.Single().Amount.ShouldBe(100_000m);
        revenue.Single().BranchId.ShouldBe(c.BranchId);
    }

    [Fact]
    public async Task Paid_order_for_a_full_class_is_flagged_for_refund()
    {
        var c = await CenterAsync("full", capacity: 1);
        var other = (await (await c.Admin.PostAsJsonAsync("/api/crm/students", new { fullName = "Khác" }, Ct)).Content.ReadFromJsonAsync<IdView>(Ct))!;
        (await c.Admin.PostAsJsonAsync($"/api/academic/classes/{c.ClassId}/enrollments", new { studentId = other.Id, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var order = await CreateOrderAsync(c, c.ClassId);
        await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order.Id}/payments", new { amount = 2_400_000m, method = "cash" }, Ct);
        await _api.DrainOutboxAsync();

        var orders = (await c.Admin.GetItemsAsync<OrderView>($"/api/billing/orders?studentId={c.StudentId}", Ct))!;
        orders.Single().Status.ShouldBe("RefundRequired");
    }

    [Fact]
    public async Task Ledger_lines_cannot_be_updated_or_deleted_by_the_application_role()
    {
        await using var connection = new NpgsqlConnection(db.ModuleConnectionString("billing"));
        await connection.OpenAsync(Ct);
        var update = await Should.ThrowAsync<PostgresException>(() =>
            new NpgsqlCommand("UPDATE billing.credit_ledger_entry SET credits = 0", connection).ExecuteNonQueryAsync(Ct));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var delete = await Should.ThrowAsync<PostgresException>(() =>
            new NpgsqlCommand("DELETE FROM billing.credit_ledger_entry", connection).ExecuteNonQueryAsync(Ct));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task<Konnyaku.Billing.Contracts.ConsumeResult> ConsumeAsync(Center c, Guid enrollmentId, Guid attendanceId, DateOnly date)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(c.TenantId);
        var result = await scope.ServiceProvider.GetRequiredService<ConsumeCreditHandler>()
            .HandleAsync(new ConsumeCreditCommand(enrollmentId, attendanceId, c.BranchId, date), Ct);
        return result.Value;
    }

    private async Task<OrderView> CreateOrderAsync(Center c, Guid classId)
    {
        var response = await c.Admin.PostAsJsonAsync("/api/billing/orders", new { studentId = c.StudentId, packageId = c.PackageId, classId }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<OrderView>(Ct))!;
    }

    private async Task<Center> CenterAsync(string prefix, int capacity)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);
        async Task<Guid> Post(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<IdView>(Ct))!.Id;
        }

        var branch = await Post("/api/organization/branches", new { code = "Q1", name = new { vi = "Q1" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false });
        var program = await Post("/api/academic/programs", new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false });
        var level = await Post($"/api/academic/programs/{program}/levels", new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 24, sessionMinutes = 90 });
        var cls = await Post("/api/academic/classes", new { code = "EN-A1-01", courseLevelId = level, branchId = branch, deliveryMode = "Offline", capacity, startsOn = "2027-01-04" });
        var package = await Post("/api/billing/packages", new { code = "A1-24", name = new { vi = "Gói 24 buổi" }, paidCredits = 24, bonusCredits = 2, price = 2_400_000m, currency = "VND" });
        var student = await Post("/api/crm/students", new { fullName = "Học viên" });
        return new Center(tenant.Id, admin, branch, cls, package, student);
    }

    private sealed record Center(Guid TenantId, HttpClient Admin, Guid BranchId, Guid ClassId, Guid PackageId, Guid StudentId);

    private sealed record IdView(Guid Id);

    private sealed record OrderView(Guid Id, string Status);

    private sealed record RosterView(Guid EnrollmentId, Guid StudentId);

    private sealed record CreditView(Guid OrderId, Guid? EnrollmentId, decimal PaidCredits, decimal BonusCredits);

    private sealed record RevenueView(Guid? BranchId, string Currency, decimal Lessons, decimal Amount);
}
