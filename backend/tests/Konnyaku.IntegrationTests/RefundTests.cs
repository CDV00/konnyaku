using System.Net;
using System.Net.Http.Json;
using Konnyaku.Billing.Application;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Refunds (class full, leaving mid-course), numbered receipts and vouchers, their PDF and print log, and who may do it.
/// Package: 24 paid + 2 bonus lessons for 2,400,000 VND (100,000 per paid lesson).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RefundTests(PostgresFixture db) : IAsyncDisposable
{
    private const decimal Price = 2_400_000m;
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Order_for_a_full_class_is_refunded_in_full_once()
    {
        var c = await CenterAsync("rffull", capacity: 1);
        await c.EnrollNewStudentAsync(c.ClassA, "Đã có chỗ");
        var (order, student) = await PaidOrderAsync(c, c.ClassA);
        await _api.DrainOutboxAsync();
        (await OrderAsync(c, order)).Status.ShouldBe("RefundRequired");

        var quote = (await c.Admin.GetFromJsonAsync<QuoteView>($"/api/billing/orders/{order}/refund-quote", Ct))!;
        quote.Refundable.ShouldBeTrue();
        quote.Kind.ShouldBe("ClassUnavailable");
        quote.SuggestedAmount.ShouldBe(Price);
        quote.BonusCreditsLeft.ShouldBe(2m);

        // Reason is required.
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/refunds", new { method = "bank_transfer" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.refund_reason_required");

        var refund = await RefundAsync(c.Admin, order, new { method = "bank_transfer", reference = "FT123", reason = "Lớp đầy" });
        refund.Amount.ShouldBe(Price);
        refund.RetainedAmount.ShouldBe(0m);
        refund.ReceiptCode.ShouldBe("PC-000001");
        refund.PaidCreditsReturned.ShouldBe(24m);
        refund.BonusCreditsForfeited.ShouldBe(2m);
        refund.ApprovedByName.ShouldBe("Admin Test");

        var refunded = await OrderAsync(c, order);
        refunded.Status.ShouldBe("Refunded");
        refunded.AmountRefunded.ShouldBe(Price);
        await (await c.Admin.GetAsync($"/api/billing/orders?status=refunded&studentId={student}", Ct)).ShouldSucceed();
        (await c.Admin.GetItemsAsync<OrderRow>("/api/billing/orders?status=refunded", Ct))!.Select(o => o.Id).ShouldBe([order]);

        // Reversing lines, never an edit: purchase and bonus stay, refund lines take them back out.
        var ledger = (await c.Admin.GetFromJsonAsync<LedgerLine[]>($"/api/billing/accounts/{order}/ledger", Ct))!;
        ledger.Select(l => (l.EntryType, l.CreditKind, l.Credits)).ShouldBe(
            [("Purchase", "Paid", 24m), ("Bonus", "Bonus", 2m), ("Refund", "Paid", -24m), ("Refund", "Bonus", -2m)], ignoreOrder: true);
        var account = (await c.Admin.GetFromJsonAsync<CreditView[]>($"/api/billing/students/{student}/credits", Ct))!.Single();
        (account.PaidCredits, account.BonusCredits).ShouldBe((0m, 0m));

        // Twice: refused.
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/refunds", new { reason = "Lại" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "billing.order_already_refunded");
        await (await c.Admin.PostAsJsonAsync($"/api/billing/accounts/{order}/adjustments", new { credits = 1, kind = "Paid", reason = "x" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "billing.order_already_refunded");

        var history = await c.HistoryAsync("billing", order);
        history.ShouldContain(h => h.EntityType == "Refund" && h.Action == "Created" && h.ActorName == "Admin Test");
        history.ShouldContain(h => h.Changes.Any(f => f.Field == "status" && f.New == "Refunded"));
    }

    [Fact]
    public async Task Leaving_mid_course_refunds_unused_paid_lessons_and_frees_the_seat()
    {
        var c = await CenterAsync("rfmid", capacity: 5);
        var (order, student) = await PaidOrderAsync(c, c.ClassA);
        await _api.DrainOutboxAsync();
        var enrollment = (await c.Admin.GetFromJsonAsync<RosterView[]>($"/api/academic/classes/{c.ClassA}/roster", Ct))!.Single().EnrollmentId;
        for (var i = 0; i < 5; i++)
        {
            await ConsumeAsync(c, enrollment, new DateOnly(2027, 1, 5 + i));
        }

        var quote = (await c.Admin.GetFromJsonAsync<QuoteView>($"/api/billing/orders/{order}/refund-quote", Ct))!;
        quote.Kind.ShouldBe("Withdrawal");
        quote.PaidCreditsLeft.ShouldBe(19m);
        quote.BonusCreditsLeft.ShouldBe(2m);
        quote.SuggestedAmount.ShouldBe(1_900_000m);

        // Never more than what was received.
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/refunds", new { amount = Price + 1, reason = "Chuyển nhà" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.refund_amount_invalid");

        // The accountant keeps a 100,000 fee: it becomes revenue on the refund day, for the branch that collected.
        var refund = await RefundAsync(c.Admin, order, new { amount = 1_800_000m, method = "cash", reason = "Chuyển nhà, trừ phí 100.000" });
        refund.Kind.ShouldBe("Withdrawal");
        refund.SuggestedAmount.ShouldBe(1_900_000m);
        refund.RetainedAmount.ShouldBe(100_000m);
        refund.PaidCreditsReturned.ShouldBe(19m);
        refund.BonusCreditsForfeited.ShouldBe(2m);

        var day = refund.LocalDate;
        var lessons = (await c.Admin.GetFromJsonAsync<RevenueView[]>("/api/billing/reports/recognized-revenue?from=2027-01-01&to=2027-01-31", Ct))!.Single();
        (lessons.Lessons, lessons.Amount).ShouldBe((5m, 500_000m));
        var refundDay = (await c.Admin.GetFromJsonAsync<RevenueView[]>($"/api/billing/reports/recognized-revenue?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", Ct))!.Single();
        refundDay.BranchId.ShouldBe(c.BranchA);
        (refundDay.LessonAmount, refundDay.RefundAdjustment, refundDay.Amount).ShouldBe((0m, 100_000m, 100_000m));

        // Received 2,400,000 = 500,000 taught + 100,000 kept + 1,800,000 paid back.
        await _api.DrainOutboxAsync();
        (await c.Admin.GetFromJsonAsync<RosterView[]>($"/api/academic/classes/{c.ClassA}/roster", Ct))!.ShouldBeEmpty();
        (await c.ClassAsync(c.ClassA)).EnrolledCount.ShouldBe(0);
        (await c.Admin.GetFromJsonAsync<CreditView[]>($"/api/billing/students/{student}/credits", Ct))!.Single().PaidCredits.ShouldBe(0m);
    }

    [Fact]
    public async Task Order_paid_in_part_refunds_what_was_received()
    {
        var c = await CenterAsync("rfpart", capacity: 5);
        var order = await OrderAsync(c, c.StudentId, c.ClassA);
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = 500_000m, method = "cash" }, Ct)).ShouldSucceed();
        (await c.Admin.GetItemsAsync<OrderRow>("/api/billing/orders?status=partial", Ct))!.Select(o => o.Id).ShouldBe([order]);

        var quote = (await c.Admin.GetFromJsonAsync<QuoteView>($"/api/billing/orders/{order}/refund-quote", Ct))!;
        (quote.Kind, quote.SuggestedAmount).ShouldBe(("Cancellation", 500_000m));
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/refunds", new { amount = 600_000m, reason = "Nghỉ" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.refund_amount_invalid");
        (await RefundAsync(c.Admin, order, new { reason = "Nghỉ trước khai giảng" })).Amount.ShouldBe(500_000m);
        (await OrderAsync(c, order)).Status.ShouldBe("Refunded");
    }

    [Fact]
    public async Task Receipt_numbers_are_consecutive_without_gaps_or_repeats_under_concurrency()
    {
        var c = await CenterAsync("rfseq", capacity: 30);
        var orders = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            orders.Add(await OrderAsync(c, c.StudentId, null));
        }

        // A failed payment (overpay) must not use a number.
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{orders[0]}/payments", new { amount = Price * 2, method = "cash" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.payment_amount_invalid");

        var responses = await Task.WhenAll(orders.Select(o =>
            c.Admin.PostAsJsonAsync($"/api/billing/orders/{o}/payments", new { amount = 100_000m, method = "cash" }, Ct)));
        foreach (var response in responses)
        {
            await response.ShouldSucceed();
        }

        var numbers = new List<long>();
        foreach (var o in orders)
        {
            numbers.AddRange((await c.Admin.GetFromJsonAsync<PaymentRow[]>($"/api/billing/orders/{o}/payments", Ct))!.Select(p => p.ReceiptNo));
        }

        numbers.Order().ShouldBe(Enumerable.Range(1, 12).Select(n => (long)n));

        // Refund vouchers have their own counter.
        var refunds = await Task.WhenAll(orders.Take(4).Select(o =>
            c.Admin.PostAsJsonAsync($"/api/billing/orders/{o}/refunds", new { reason = "Nghỉ" }, Ct)));
        var codes = new List<string>();
        foreach (var response in refunds)
        {
            await response.ShouldSucceed();
            codes.Add((await response.Content.ReadFromJsonAsync<RefundRow>(Ct))!.ReceiptCode);
        }

        codes.Order().ShouldBe(["PC-000001", "PC-000002", "PC-000003", "PC-000004"]);
    }

    [Fact]
    public async Task Refunds_need_the_refund_permission_for_the_order_branch()
    {
        var c = await CenterAsync("rfperm", capacity: 5);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB, capacity: 5);
        var (orderA, _) = await PaidOrderAsync(c, c.ClassA);
        var (orderB, _) = await PaidOrderAsync(c, classB);
        var (_, consultant) = await c.AddStaffAsync("Consultant");
        var (_, accountantA) = await c.AddStaffAsync("Accountant", c.BranchA);

        // Consultant: no refund permission at all.
        await (await consultant.GetAsync($"/api/billing/orders/{orderA}/refund-quote", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await consultant.PostAsJsonAsync($"/api/billing/orders/{orderA}/refunds", new { reason = "x" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");

        // Accountant of branch A: not branch B's orders, payments, adjustments, receipts — nor center-wide prices.
        await (await accountantA.PostAsJsonAsync($"/api/billing/orders/{orderB}/refunds", new { reason = "x" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await accountantA.GetAsync($"/api/billing/orders/{orderB}/refund-quote", Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await accountantA.PostAsJsonAsync($"/api/billing/accounts/{orderB}/adjustments", new { credits = 1, kind = "Paid", reason = "x" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        var paymentB = (await c.Admin.GetFromJsonAsync<PaymentRow[]>($"/api/billing/orders/{orderB}/payments", Ct))!.Single();
        await (await accountantA.GetAsync($"/api/billing/receipts/receipt/{paymentB.Id}/pdf", Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await accountantA.PostAsJsonAsync("/api/billing/packages",
                new { code = "X1", name = new { vi = "X" }, paidCredits = 1, bonusCredits = 0, price = 1m, currency = "VND" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        var orderBPending = await OrderAsync(c, c.StudentId, classB);
        await (await accountantA.PostAsJsonAsync($"/api/billing/orders/{orderBPending}/payments", new { amount = 1000m, method = "cash" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // ... but their own branch, with the change history naming them.
        var refund = await RefundAsync(accountantA, orderA, new { reason = "Nghỉ học" });
        refund.ApprovedByName.ShouldBe("Accountant Test");
        (await c.HistoryAsync("billing", orderA)).ShouldContain(h => h.EntityType == "Refund" && h.ActorName == "Accountant Test");
    }

    [Fact]
    public async Task Receipt_pdf_is_logged_limited_and_reachable_by_its_own_learner_only()
    {
        var c = await CenterAsync("rfpdf", capacity: 5);
        var learner = await LearnerAsync(c, "Phụ huynh An");
        var studentId = await c.PostIdAsync("/api/crm/students", new { fullName = "Nguyễn Văn An", userId = learner.UserId });
        var order = await OrderAsync(c, studentId, c.ClassA);
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = 1_500_000m, method = "cash" }, Ct)).ShouldSucceed();
        var payment = (await c.Admin.GetFromJsonAsync<PaymentRow[]>($"/api/billing/orders/{order}/payments", Ct))!.Single();
        payment.ReceiptCode.ShouldBe("PT-000001");
        payment.ReceivedByName.ShouldBe("Admin Test");

        var receipt = (await c.Admin.GetFromJsonAsync<ReceiptRow>($"/api/billing/receipts/receipt/{payment.Id}", Ct))!;
        receipt.StudentName.ShouldBe("Nguyễn Văn An");
        receipt.AmountInWords.ShouldBe("Một triệu năm trăm nghìn đồng");
        receipt.IssuedByName.ShouldBe("Admin Test");

        var pdf = await c.Admin.GetAsync($"/api/billing/receipts/receipt/{payment.Id}/pdf?download=true", Ct);
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdf.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await pdf.Content.ReadAsByteArrayAsync(Ct))[..4].ShouldBe("%PDF"u8.ToArray());

        // The learner sees their history and downloads their own receipt; another learner cannot.
        var history = (await learner.Client.GetFromJsonAsync<MyPaymentRow[]>("/api/billing/my/payments", Ct))!;
        history.Single().Code.ShouldBe("PT-000001");
        (await learner.Client.GetAsync($"/api/billing/my/receipts/receipt/{payment.Id}/pdf", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stranger = await LearnerAsync(c, "Người khác");
        await (await stranger.Client.GetAsync($"/api/billing/my/receipts/receipt/{payment.Id}/pdf", Ct))
            .ShouldFailWith(HttpStatusCode.NotFound, "billing.receipt_not_found");
        (await learner.Client.GetAsync($"/api/billing/receipts/receipt/{payment.Id}/pdf", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var prints = (await c.Admin.GetFromJsonAsync<PrintRow[]>($"/api/billing/receipts/receipt/{payment.Id}/prints", Ct))!;
        prints.Select(p => (p.Action, p.Audience, p.ActorName)).ShouldBe([("print", "learn", "Phụ huynh An"), ("download", "staff", "Admin Test")]);

        // The print log is append-only, like the ledger.
        await using var connection = new NpgsqlConnection(db.ModuleConnectionString("billing"));
        await connection.OpenAsync(Ct);
        var delete = await Should.ThrowAsync<PostgresException>(() =>
            new NpgsqlCommand("DELETE FROM billing.receipt_print_log", connection).ExecuteNonQueryAsync(Ct));
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Per person and document: 20 prints a day.
        for (var i = prints.Count(p => p.Audience == "staff"); i < 20; i++)
        {
            (await c.Admin.GetAsync($"/api/billing/receipts/receipt/{payment.Id}/pdf", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await (await c.Admin.GetAsync($"/api/billing/receipts/receipt/{payment.Id}/pdf", Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "billing.receipt_print_limit");
    }

    private async Task<RefundRow> RefundAsync(HttpClient client, Guid orderId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/billing/orders/{orderId}/refunds", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RefundRow>(Ct))!;
    }

    private async Task<(Guid OrderId, Guid StudentId)> PaidOrderAsync(RefundCenter c, Guid classId)
    {
        var student = await c.PostIdAsync("/api/crm/students", new { fullName = "Học viên" });
        var order = await OrderAsync(c, student, classId);
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = Price, method = "cash" }, Ct)).ShouldSucceed();
        return (order, student);
    }

    private static async Task<Guid> OrderAsync(RefundCenter c, Guid studentId, Guid? classId) =>
        await c.PostIdAsync("/api/billing/orders", new { studentId, packageId = c.PackageId, classId });

    private static async Task<OrderRow> OrderAsync(RefundCenter c, Guid orderId) =>
        (await c.Admin.GetItemsAsync<OrderRow>("/api/billing/orders", Ct))!.Single(o => o.Id == orderId);

    private async Task ConsumeAsync(RefundCenter c, Guid enrollmentId, DateOnly date)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(c.TenantId);
        var result = await scope.ServiceProvider.GetRequiredService<ConsumeCreditHandler>()
            .HandleAsync(new ConsumeCreditCommand(enrollmentId, Guid.NewGuid(), c.BranchA, date), Ct);
        result.Value.Consumed.ShouldBeTrue();
    }

    private async Task<(Guid UserId, HttpClient Client)> LearnerAsync(RefundCenter c, string name)
    {
        var account = (await (await c.Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = name, role = "Student" }, Ct))
            .Content.ReadFromJsonAsync<LearnerAccountView>(Ct))!;
        var login = (await (await _api.ClientFor(c.Slug).PostAsJsonAsync("/api/identity/learn/login",
            new { username = account.Username, password = account.TemporaryPassword }, Ct)).Content.ReadFromJsonAsync<SignInDto>(Ct))!;
        return (account.UserId, _api.ClientFor(c.Slug, login.AccessToken));
    }

    private async Task<RefundCenter> CenterAsync(string prefix, int capacity)
    {
        var center = await EditCenter.CreateAsync(_api, prefix);
        var classA = await center.CreateClassAsync("CLS-A", center.BranchA, capacity: capacity);
        var package = await center.PostIdAsync("/api/billing/packages",
            new { code = "A1-24", name = new { vi = "Gói 24 buổi" }, paidCredits = 24, bonusCredits = 2, price = Price, currency = "VND" });
        var student = await center.PostIdAsync("/api/crm/students", new { fullName = "Học viên chính" });
        var tenantId = (await center.Admin.GetFromJsonAsync<TenantIdView>("/api/platform/tenant", Ct))!.Id;
        return new RefundCenter(center, tenantId, classA, package, student);
    }

    private sealed class RefundCenter(EditCenter center, Guid tenantId, Guid classA, Guid packageId, Guid studentId)
    {
        public Guid TenantId { get; } = tenantId;
        public Guid ClassA { get; } = classA;
        public Guid PackageId { get; } = packageId;
        public Guid StudentId { get; } = studentId;
        public string Slug => center.Slug;
        public HttpClient Admin => center.Admin;
        public Guid BranchA => center.BranchA;
        public Guid BranchB => center.BranchB;

        public Task<Guid> PostIdAsync(string path, object body) => center.PostIdAsync(path, body);

        public Task<Guid> EnrollNewStudentAsync(Guid classId, string name) => center.EnrollNewStudentAsync(classId, name);

        public Task<Guid> CreateClassAsync(string code, Guid branchId, int capacity) => center.CreateClassAsync(code, branchId, capacity: capacity);

        public Task<(Guid UserId, HttpClient Client)> AddStaffAsync(string role, params Guid[] branchIds) => center.AddStaffAsync(role, branchIds);

        public Task<ClassView> ClassAsync(Guid classId) => center.ClassAsync(classId);

        public Task<HistoryEntry[]> HistoryAsync(string module, Guid id) => center.HistoryAsync(module, id);
    }

    private sealed record TenantIdView(Guid Id);

    private sealed record LearnerAccountView(Guid UserId, string Username, string TemporaryPassword);

    private sealed record QuoteView(Guid OrderId, bool Refundable, string? Kind, decimal AmountReceived, decimal PaidCreditsLeft, decimal BonusCreditsLeft,
        decimal UnusedValue, decimal SuggestedAmount);

    private sealed record RefundRow(Guid Id, string Kind, string ReceiptCode, decimal Amount, decimal SuggestedAmount, decimal RetainedAmount,
        decimal PaidCreditsReturned, decimal BonusCreditsForfeited, string? ApprovedByName, DateOnly LocalDate);

    private sealed record OrderRow(Guid Id, string Status, decimal AmountReceived, decimal AmountRefunded);

    private sealed record PaymentRow(Guid Id, long ReceiptNo, string ReceiptCode, string? ReceivedByName);

    private sealed record LedgerLine(string EntryType, string CreditKind, decimal Credits);

    private sealed record CreditView(Guid OrderId, decimal PaidCredits, decimal BonusCredits);

    private sealed record RosterView(Guid EnrollmentId, Guid StudentId);

    private sealed record RevenueView(Guid? BranchId, decimal Lessons, decimal LessonAmount, decimal RefundAdjustment, decimal Amount);

    private sealed record ReceiptRow(string Code, string StudentName, string AmountInWords, string? IssuedByName);

    private sealed record MyPaymentRow(string Kind, Guid Id, string Code);

    private sealed record PrintRow(string Action, string Audience, string? ActorName);
}
