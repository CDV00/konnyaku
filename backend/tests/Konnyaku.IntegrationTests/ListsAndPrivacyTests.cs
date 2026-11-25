using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Konnyaku.Crm.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Lists (paging, accent-insensitive search, filters) and personal data protection: masking, logged reveal, export
/// limits, data subject requests, erasure that keeps the lesson ledger, and lead retention.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ListsAndPrivacyTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db)
    {
        Settings = new Dictionary<string, string> { ["Privacy:ExportMaxRows"] = "5", ["Privacy:ExportsPerDay"] = "2" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Student_list_is_paged_and_search_ignores_accents()
    {
        var c = await EditCenter.CreateAsync(_api, "lists");
        await c.PostIdAsync("/api/crm/students", new { fullName = "Nguyễn Văn Ân", phone = "0901 234 567", homeBranchId = c.BranchA });
        await c.PostIdAsync("/api/crm/students", new { fullName = "Lê An", email = "le.an@example.com", homeBranchId = c.BranchB });
        await c.PostIdAsync("/api/crm/students", new { fullName = "Hồ Bình" });
        for (var i = 0; i < 27; i++)
        {
            await c.PostIdAsync("/api/crm/students", new { fullName = $"Học viên {i:00}", homeBranchId = c.BranchA });
        }

        var first = await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?pageSize=10", Ct);
        (first.Total, first.Items.Length, first.PageNumber).ShouldBe((30, 10, 1));
        var last = await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?pageSize=10&page=3", Ct);
        last.Items.Length.ShouldBe(10);
        last.Items.Select(s => s.Id).Intersect(first.Items.Select(s => s.Id)).ShouldBeEmpty();
        (await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?pageSize=10&page=9", Ct)).Items.ShouldBeEmpty();
        (await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?pageSize=100000", Ct)).PageSize.ShouldBe(200);

        // "an" and "Ân" find the same students: name without accents, either way round.
        var plain = await c.Admin.GetItemsAsync<StudentRow>("/api/crm/students?q=an", Ct);
        var accented = await c.Admin.GetItemsAsync<StudentRow>($"/api/crm/students?q={Uri.EscapeDataString("Ân")}", Ct);
        plain.Select(s => s.FullName).Order().ShouldBe(["Lê An", "Nguyễn Văn Ân"]);
        accented.Select(s => s.Id).Order().ShouldBe(plain.Select(s => s.Id).Order());
        (await c.Admin.GetItemsAsync<StudentRow>($"/api/crm/students?q={Uri.EscapeDataString("BINH")}", Ct)).Single().FullName.ShouldBe("Hồ Bình");

        // Phone typed any way, email, branch filter, sort.
        (await c.Admin.GetItemsAsync<StudentRow>($"/api/crm/students?q={Uri.EscapeDataString("+84 901 234")}", Ct)).Single().FullName.ShouldBe("Nguyễn Văn Ân");
        (await c.Admin.GetItemsAsync<StudentRow>("/api/crm/students?q=le.an@", Ct)).Single().FullName.ShouldBe("Lê An");
        (await c.Admin.GetPageAsync<StudentRow>($"/api/crm/students?branch={c.BranchB}", Ct)).Total.ShouldBe(1);
        (await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?branch=none", Ct)).Total.ShouldBe(1);
        var desc = await c.Admin.GetItemsAsync<StudentRow>("/api/crm/students?sort=name&dir=desc&pageSize=1", Ct);
        desc.Single().FullName.ShouldBe("Nguyễn Văn Ân");
        // A LIKE wildcard typed in the box is a character, not "anything".
        (await c.Admin.GetPageAsync<StudentRow>("/api/crm/students?q=%25", Ct)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task Contact_details_are_masked_and_revealed_only_to_record_keepers_with_a_log_entry()
    {
        var c = await EditCenter.CreateAsync(_api, "mask");
        var student = await c.PostIdAsync("/api/crm/students",
            new { fullName = "Phạm Thu Hà", birthYear = 2012, guardianName = "Phạm Văn Minh", phone = "0912345678", email = "ha.pham@example.com" });
        var (_, accountant) = await c.AddStaffAsync("Accountant");
        var (consultantId, consultant) = await c.AddStaffAsync("Consultant");

        var row = (await accountant.GetItemsAsync<StudentRow>("/api/crm/students", Ct)).Single();
        row.ShouldBe(new StudentRow(student, "Phạm Thu Hà", "201*", "Phạm V** M**", "09xx xxx 678", "h***@example.com", false));
        (await accountant.PostAsJsonAsync($"/api/crm/students/{student}/reveal", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await accountant.GetAsync("/api/crm/students/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var revealed = await consultant.PostAsJsonAsync($"/api/crm/students/{student}/reveal", new { purpose = "contact" }, Ct);
        revealed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await revealed.Content.ReadFromJsonAsync<ContactRow>(Ct)).ShouldBe(new ContactRow(2012, "Phạm Văn Minh", "0912345678", "ha.pham@example.com"));

        // Only the tenant admin reads the log; it names who, what and why, never the values.
        (await consultant.GetAsync("/api/crm/privacy/access-log", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var log = await c.Admin.GetItemsAsync<AccessRow>($"/api/crm/privacy/access-log?subject={student}", Ct);
        log.Single().ShouldSatisfyAllConditions(
            a => a.Action.ShouldBe("Reveal"), a => a.ActorId.ShouldBe(consultantId), a => a.Purpose.ShouldBe("contact"),
            a => a.SubjectName.ShouldBe("Phạm Thu Hà"));
        (await c.Admin.GetStringAsync("/api/crm/privacy/access-log", Ct)).ShouldNotContain("0912345678");
    }

    [Fact]
    public async Task Exports_are_limited_logged_and_stamped()
    {
        var c = await EditCenter.CreateAsync(_api, "export");
        for (var i = 0; i < 7; i++)
        {
            await c.PostIdAsync("/api/crm/students", new { fullName = $"Export {i}", phone = $"090000000{i}", homeBranchId = i < 3 ? c.BranchA : c.BranchB });
        }

        var (_, consultant) = await c.AddStaffAsync("Consultant");
        (await consultant.GetAsync("/api/crm/students/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Seven rows, five allowed: filter first.
        await (await c.Admin.GetAsync("/api/crm/students/export", Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "crm.export_too_large");
        var file = await c.Admin.GetAsync($"/api/crm/students/export?branch={c.BranchA}&locale=en", Ct);
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var csv = Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync(Ct)).TrimStart('﻿');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("Name,Birth year,Guardian,Phone");
        lines.Length.ShouldBe(5); // header, 3 rows, stamp
        lines[1].ShouldContain("0900000000");
        lines[^1].Trim('"').ShouldStartWith("Exported by Admin T** at ");
        lines[^1].ShouldContain("3 rows");

        // A branch manager exports their branch only.
        var (_, manager) = await c.AddStaffAsync("BranchManager", c.BranchB);
        var managerFile = Encoding.UTF8.GetString(await manager.GetByteArrayAsync("/api/crm/students/export", Ct));
        managerFile.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(6); // header, 4 rows, stamp

        // Printing is logged too and returns masked rows.
        var print = (await c.Admin.GetFromJsonAsync<PrintRows>($"/api/crm/students/print?branch={c.BranchA}", Ct))!;
        (print.Total, print.PrintedBy).ShouldBe((3, "Admin T**"));
        print.Rows.ShouldAllBe(r => r.Phone!.Contains("xx"));

        // The admin exported and printed once each: the limit (2 a day in this test) is reached.
        await (await c.Admin.GetAsync($"/api/crm/students/export?branch={c.BranchA}", Ct)).ShouldFailWith(HttpStatusCode.Conflict, "crm.export_limit_reached");

        var log = await c.Admin.GetItemsAsync<AccessRow>("/api/crm/privacy/access-log?action=Export", Ct);
        log.Select(a => a.RowCount).Order().ShouldBe([3, 4]);
        log.ShouldContain(a => a.Filter.ContainsKey("branch"));
    }

    [Fact]
    public async Task Erasure_anonymizes_the_student_but_keeps_ledger_and_orders()
    {
        var c = await EditCenter.CreateAsync(_api, "erase");
        var account = (await (await c.Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "Đỗ Minh Khang", role = "Student" }, Ct))
            .Content.ReadFromJsonAsync<LearnerAccount>(Ct))!;
        var student = await c.PostIdAsync("/api/crm/students",
            new { fullName = "Đỗ Minh Khang", birthYear = 2013, guardianName = "Đỗ Văn Long", phone = "0987654321", userId = account.UserId });
        await (await c.Admin.PutAsJsonAsync($"/api/crm/students/{student}",
            new { fullName = "Đỗ Minh Khang", birthYear = 2013, guardianName = "Đỗ Văn Long", phone = "0987654322" }, Ct)).ShouldSucceed();
        var package = await c.PostIdAsync("/api/billing/packages", new { code = "P4", name = new { vi = "4 buổi" }, paidCredits = 4, bonusCredits = 0, price = 800000, currency = "VND" });
        var order = await c.PostIdAsync("/api/billing/orders", new { studentId = student, packageId = package });
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = 800000, method = "cash" }, Ct)).ShouldSucceed();
        await _api.DrainOutboxAsync();
        var creditsBefore = await c.Admin.GetStringAsync($"/api/billing/students/{student}/credits", Ct);
        creditsBefore.ShouldContain("4");

        // A data subject request, with its 72-hour deadline.
        var created = await c.Admin.PostAsJsonAsync("/api/crm/privacy/requests", new { subjectType = "student", subjectId = student, type = "Erasure", note = "Phụ huynh gửi email" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var requestId = (await created.Content.ReadFromJsonAsync<IdView>(Ct))!.Id;
        var request = (await c.Admin.GetItemsAsync<RequestRow>("/api/crm/privacy/requests?status=Open", Ct)).Single();
        (request.DueAt - request.ReceivedAt).ShouldBe(TimeSpan.FromHours(72));
        request.SubjectName.ShouldBe("Đỗ Minh Khang");

        // Access export holds every module's data.
        var export = JsonDocument.Parse(await c.Admin.GetStringAsync($"/api/crm/privacy/students/{student}/export", Ct)).RootElement.GetProperty("data");
        export.GetProperty("crm").GetProperty("profile").GetProperty("phone").GetString().ShouldBe("0987654322");
        export.GetProperty("billing").GetProperty("ledger").GetArrayLength().ShouldBeGreaterThan(0);
        export.GetProperty("identity").GetProperty("usernames").GetArrayLength().ShouldBe(1);

        // An active student is not erased; deactivate first.
        await (await c.Admin.PostAsJsonAsync($"/api/crm/privacy/students/{student}/anonymize", new { requestId }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "crm.student_active");
        await (await c.Admin.PostAsJsonAsync($"/api/crm/students/{student}/deactivate", new { }, Ct)).ShouldSucceed();
        (await c.Admin.PostAsJsonAsync($"/api/crm/privacy/students/{student}/anonymize", new { requestId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _api.DrainOutboxAsync();

        var row = (await c.Admin.GetItemsAsync<StudentRow>($"/api/crm/students?ids={student}", Ct)).Single();
        row.ShouldSatisfyAllConditions(
            r => r.FullName.ShouldStartWith("#"), r => r.Phone.ShouldBeNull(), r => r.GuardianName.ShouldBeNull(), r => r.BirthYear.ShouldBeNull(),
            r => r.Anonymized.ShouldBeTrue());
        (await c.Admin.GetPageAsync<StudentRow>($"/api/crm/students?q={Uri.EscapeDataString("Khang")}", Ct)).Total.ShouldBe(0);
        // Ledger and orders untouched; history keeps the events, not the values.
        (await c.Admin.GetStringAsync($"/api/billing/students/{student}/credits", Ct)).ShouldBe(creditsBefore);
        (await c.Admin.GetPageAsync<IdView>($"/api/billing/orders?studentId={student}", Ct)).Total.ShouldBe(1);
        var history = await c.HistoryAsync("crm", student);
        history.ShouldNotBeEmpty();
        history.SelectMany(h => h.Changes).ShouldAllBe(f => f.Old == null && f.New == null);
        // The learner account can no longer sign in, and the request is closed.
        (await _api.ClientFor(c.Slug).PostAsJsonAsync("/api/identity/learn/login", new { username = account.Username, password = account.TemporaryPassword }, Ct))
            .IsSuccessStatusCode.ShouldBeFalse();
        (await c.Admin.GetItemsAsync<RequestRow>("/api/crm/privacy/requests", Ct)).Single().Status.ShouldBe("Completed");
        (await c.Admin.GetItemsAsync<AccessRow>($"/api/crm/privacy/access-log?action=Anonymize", Ct)).Single().SubjectId.ShouldBe(student);
        await using (var connection = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT display_name, email FROM identity.\"user\" WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", account.UserId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetString(0).ShouldStartWith("#");
        }
    }

    [Fact]
    public async Task Requests_need_the_privacy_permission_and_rejections_need_a_reason()
    {
        var c = await EditCenter.CreateAsync(_api, "dsr");
        var student = await c.PostIdAsync("/api/crm/students", new { fullName = "HV" });
        var (_, manager) = await c.AddStaffAsync("BranchManager");
        (await manager.PostAsJsonAsync("/api/crm/privacy/requests", new { subjectType = "student", subjectId = student, type = "Access" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await (await c.Admin.PostAsJsonAsync("/api/crm/privacy/requests", new { subjectType = "student", subjectId = Guid.NewGuid(), type = "Access" }, Ct))
            .ShouldFailWith(HttpStatusCode.NotFound, "crm.subject_not_found");
        var id = (await (await c.Admin.PostAsJsonAsync("/api/crm/privacy/requests", new { subjectType = "student", subjectId = student, type = "Access" }, Ct))
            .Content.ReadFromJsonAsync<IdView>(Ct))!.Id;
        await (await c.Admin.PostAsJsonAsync($"/api/crm/privacy/requests/{id}/resolve", new { status = "Rejected" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "crm.request_reason_required");
        (await c.Admin.PostAsJsonAsync($"/api/crm/privacy/requests/{id}/resolve", new { status = "Completed" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await c.Admin.PostAsJsonAsync($"/api/crm/privacy/requests/{id}/resolve", new { status = "Completed" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "crm.request_closed");
    }

    [Fact]
    public async Task Leads_past_retention_are_anonymized_by_the_worker_job()
    {
        var c = await EditCenter.CreateAsync(_api, "retain");
        var visitor = _api.ClientFor(c.Slug);
        foreach (var (name, phone) in new[] { ("Lead Cũ", "0911000001"), ("Lead Mới", "0911000002") })
        {
            (await visitor.PostAsJsonAsync("/api/crm/public/leads", new { studentName = name, phone, languageCode = "en", consentVersion = "v1", consentGiven = true }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var leads = await c.Admin.GetItemsAsync<LeadRow>("/api/crm/leads?q=lead%20cu", Ct);
        var old = leads.Single().Id;
        await using (var connection = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE crm.lead SET updated_at = now() - interval '25 months' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", old);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        var job = new LeadRetentionService(_api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LeadRetentionService>.Instance);
        (await job.RunOnceAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        var all = await c.Admin.GetItemsAsync<LeadRow>("/api/crm/leads?status=all", Ct);
        all.Single(l => l.Id == old).ShouldSatisfyAllConditions(l => l.Anonymized.ShouldBeTrue(), l => l.Phone.ShouldBeNull(), l => l.Status.ShouldBe("Lost"));
        all.Single(l => l.Id != old).Anonymized.ShouldBeFalse();
        var log = await c.Admin.GetItemsAsync<AccessRow>("/api/crm/privacy/access-log?action=RetentionAnonymize", Ct);
        log.Single().ShouldSatisfyAllConditions(a => a.SubjectId.ShouldBe(old), a => a.ActorId.ShouldBeNull());
    }

    [Fact]
    public async Task Class_order_and_staff_lists_share_the_page_shape_and_filters()
    {
        var c = await EditCenter.CreateAsync(_api, "pages");
        var a = await c.CreateClassAsync("EN-A1-01", c.BranchA);
        await c.CreateClassAsync("EN-A1-02", c.BranchB);
        (await c.Admin.GetPageAsync<IdView>("/api/academic/classes?q=a1-02", Ct)).Total.ShouldBe(1);
        (await c.Admin.GetPageAsync<IdView>($"/api/academic/classes?branch={c.BranchA}", Ct)).Items.Single().Id.ShouldBe(a);
        (await c.Admin.GetFromJsonAsync<IdView>($"/api/academic/classes/{a}", Ct))!.Id.ShouldBe(a);
        (await c.Admin.GetAsync($"/api/academic/classes/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var ân = await c.PostIdAsync("/api/crm/students", new { fullName = "Vũ Ân" });
        var other = await c.PostIdAsync("/api/crm/students", new { fullName = "Hồ Bảo" });
        var package = await c.PostIdAsync("/api/billing/packages", new { code = "P1", name = new { vi = "1 buổi" }, paidCredits = 1, bonusCredits = 0, price = 100000, currency = "VND" });
        await c.PostIdAsync("/api/billing/orders", new { studentId = ân, packageId = package });
        await c.PostIdAsync("/api/billing/orders", new { studentId = other, packageId = package });
        (await c.Admin.GetPageAsync<IdView>("/api/billing/orders?q=an", Ct)).Total.ShouldBe(1);
        (await c.Admin.GetPageAsync<IdView>("/api/billing/orders?status=Paid", Ct)).Total.ShouldBe(0);
        (await c.Admin.GetPageAsync<IdView>("/api/billing/orders?pageSize=1", Ct)).ShouldSatisfyAllConditions(p => p.Total.ShouldBe(2), p => p.Items.Length.ShouldBe(1));

        (await c.Admin.GetPageAsync<StaffRow>("/api/identity/staff?role=Teacher", Ct)).Total.ShouldBe(2);
        (await c.Admin.GetPageAsync<StaffRow>("/api/identity/staff?q=admin%20t", Ct)).Items.Single().DisplayName.ShouldBe("Admin Test");
    }

    private sealed record StudentRow(Guid Id, string FullName, string? BirthYear, string? GuardianName, string? Phone, string? Email, bool Anonymized);

    private sealed record LeadRow(Guid Id, string StudentName, string? Phone, string Status, bool Anonymized);

    private sealed record ContactRow(int? BirthYear, string? GuardianName, string? Phone, string? Email);

    private sealed record AccessRow(
        Guid Id, string Action, string SubjectType, Guid? SubjectId, string? SubjectName, string? Purpose, Dictionary<string, string> Filter, int? RowCount,
        Guid? ActorId);

    private sealed record PrintRows(PrintRow[] Rows, int Total, string PrintedBy);

    private sealed record PrintRow(Guid Id, string Name, string? Phone);

    private sealed record RequestRow(Guid Id, string SubjectType, string? SubjectName, string Status, DateTimeOffset ReceivedAt, DateTimeOffset DueAt);

    private sealed record LearnerAccount(Guid UserId, string Username, string TemporaryPassword);

    private sealed record StaffRow(Guid UserId, string DisplayName);
}
