using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Notification.Application;
using Konnyaku.Notification.Contracts;
using Konnyaku.Notification.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NotificationTests : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly PostgresFixture _db;
    private readonly FakeEmailSender _mail = new();
    private readonly KonnyakuApiFactory _api;

    public NotificationTests(PostgresFixture db)
    {
        _db = db;
        _api = new KonnyakuApiFactory(db)
        {
            Settings = new Dictionary<string, string> { ["Notification:RetryBaseDelaySeconds"] = "0" },
            TestServices = services => services.AddSingleton<IEmailSender>(_mail),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Session_changes_reach_students_and_teacher_in_their_language_once()
    {
        var c = await SetUpClassAsync();
        var sessions = (await c.Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{c.ClassId}/sessions", Ct))!;
        await SendAllAsync();
        _mail.To(c.EnglishEmail).Select(m => m.Subject).ShouldBe(["You joined class " + c.Code]);
        _mail.To(c.VietnameseEmail).Select(m => m.Subject).ShouldBe(["Bạn đã vào lớp " + c.Code]);

        (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[0].Id}/reschedule", new { date = "2027-03-05", startTime = "18:00" }, Ct))
            .IsSuccessStatusCode.ShouldBeTrue();
        await _api.DrainOutboxAsync();

        // The outbox delivers at least once: replay the reschedule event as if the worker crashed after handling it.
        await ExecAsync($"UPDATE scheduling.outbox_message SET processed_at = NULL WHERE tenant_id = '{c.TenantId}' AND type LIKE '%SessionRescheduledV1%'");
        await _api.DrainOutboxAsync();
        await SendAllAsync();

        var english = _mail.To(c.EnglishEmail).Where(m => m.Subject.Contains("moved")).ToList();
        english.Count.ShouldBe(1);
        english[0].Subject.ShouldBe($"Session 1 of {c.Code} moved");
        english[0].Text.ShouldContain("18:00");
        english[0].Html.ShouldContain(c.TenantName);
        english[0].FromName.ShouldBe(c.TenantName);
        _mail.To(c.VietnameseEmail).Count(m => m.Subject == $"Đổi giờ buổi 1 lớp {c.Code}").ShouldBe(1);
        var teacher = _mail.To(c.TeacherEmail).Where(m => m.Subject.StartsWith("Đổi giờ", StringComparison.Ordinal)).ToList();
        teacher.Count.ShouldBe(1);
        teacher[0].Text.ShouldContain("/teach");

        (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[1].Id}/cancel", new { reason = "Giáo viên ốm" }, Ct))
            .IsSuccessStatusCode.ShouldBeTrue();
        await _api.DrainOutboxAsync();
        await SendAllAsync();
        _mail.To(c.EnglishEmail).Single(m => m.Subject.Contains("cancelled")).Text.ShouldContain("Giáo viên ốm");
        _mail.To(c.TeacherEmail).Count(m => m.Subject.StartsWith("Hủy buổi 2", StringComparison.Ordinal)).ShouldBe(1);

        var log = (await c.Admin.GetFromJsonAsync<MessageView[]>("/api/notification/messages", Ct))!;
        log.Count(m => m.TemplateKey == "session.rescheduled").ShouldBe(3);
        log.ShouldAllBe(m => m.Status == "Sent");
        log.Single(m => m.RecipientAddress == c.EnglishEmail && m.TemplateKey == "session.rescheduled").Locale.ShouldBe("en");

        // Reminders: one per session still ahead (the cancelled one has none), due 24 hours before.
        var reminders = await ScalarAsync<long>($"SELECT count(*) FROM notification.dispatch_item WHERE tenant_id = '{c.TenantId}' AND kind = 'session_reminder'");
        reminders.ShouldBe(1);
    }

    [Fact]
    public async Task Reminder_job_reminds_everyone_of_a_session_starting_within_24_hours()
    {
        var c = await SetUpClassAsync();
        var session = (await c.Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{c.ClassId}/sessions", Ct))![0];

        // Make the reminder due now and the session start in 20 hours, as the clock would.
        var tomorrow = DateTimeOffset.UtcNow.AddHours(20);
        var local = TimeZoneInfo.ConvertTime(tomorrow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{session.Id}/reschedule",
            new { date = local.ToString("yyyy-MM-dd"), startTime = local.ToString("HH:mm") }, Ct)).IsSuccessStatusCode.ShouldBeTrue();
        await _api.DrainOutboxAsync();
        await SendAllAsync();

        _mail.To(c.EnglishEmail).Count(m => m.Subject.StartsWith("Reminder:", StringComparison.Ordinal)).ShouldBe(1);
        _mail.To(c.VietnameseEmail).Count(m => m.Subject.StartsWith("Nhắc lịch:", StringComparison.Ordinal)).ShouldBe(1);
        _mail.To(c.TeacherEmail).Count(m => m.Subject.StartsWith("Nhắc lịch:", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Transient_errors_are_retried_and_permanent_ones_stop_until_an_admin_resends()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("ntf", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);
        var flaky = $"flaky-{Guid.NewGuid():N}@example.com";
        var bounced = $"bounce-{Guid.NewGuid():N}@example.com";
        _mail.Fail(flaky, DeliveryResult.Transient("smtp_421: try later"), times: 2);
        _mail.Fail(bounced, DeliveryResult.Permanent("smtp_550: mailbox unavailable"), times: 1);

        // Another module asks through the contracts API; the same key twice queues one message.
        async Task<NotificationQueued> EnqueueAsync(string address, string key)
        {
            await using var scope = _api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.Id);
            var api = scope.ServiceProvider.GetRequiredService<INotificationApi>();
            var result = await api.EnqueueAsync(new NotificationRequest("credit.low", new NotificationRecipient(null, address, "Học viên", "vi"),
                new Dictionary<string, string> { ["studentName"] = "Học viên", ["remaining"] = "2" }, key), Ct);
            result.IsSuccess.ShouldBeTrue();
            return result.Value;
        }

        var first = await EnqueueAsync(flaky, $"test:{flaky}");
        (await EnqueueAsync(flaky, $"test:{flaky}")).ShouldBe(first with { Duplicate = true });
        await EnqueueAsync(bounced, $"test:{bounced}");
        await SendAllAsync();

        var log = (await admin.GetFromJsonAsync<MessageView[]>("/api/notification/messages", Ct))!;
        var retried = log.Single(m => m.RecipientAddress == flaky);
        (retried.Status, retried.Attempts, retried.Subject).ShouldBe(("Sent", 3, "Bạn sắp hết buổi học"));
        var failed = log.Single(m => m.RecipientAddress == bounced);
        (failed.Status, failed.Attempts).ShouldBe(("Failed", 1));
        failed.LastError!.ShouldContain("550");
        _mail.To(bounced).ShouldBeEmpty();

        // Only tenant admins read the log or resend.
        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        (await admin.PostAsJsonAsync("/api/identity/staff", new { email = teacherEmail, displayName = "GV", initialPassword = Password, roles = new[] { "Teacher" } }, Ct))
            .EnsureSuccessStatusCode();
        var teacher = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken);
        (await teacher.GetAsync("/api/notification/messages", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PostAsync($"/api/notification/messages/{failed.Id}/resend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await admin.PostAsync($"/api/notification/messages/{failed.Id}/resend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await SendAllAsync();
        _mail.To(bounced).Count.ShouldBe(1);
        (await admin.GetFromJsonAsync<MessageView[]>("/api/notification/messages?status=Sent", Ct))!
            .ShouldContain(m => m.Id == failed.Id);

        // Another tenant sees nothing of it.
        var otherEmail = $"admin-{Guid.NewGuid():N}@example.com";
        var other = await _api.CreateTenantWithAdminAsync("ntf2", otherEmail, Password);
        var otherAdmin = _api.ClientFor(other.Slug, (await _api.StaffLoginAsync(other.Slug, otherEmail, Password)).AccessToken);
        (await otherAdmin.GetFromJsonAsync<MessageView[]>("/api/notification/messages", Ct))!.ShouldBeEmpty();
        (await otherAdmin.PostAsync($"/api/notification/messages/{failed.Id}/resend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_lead_notifies_the_assigned_consultant()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("lead", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);
        var consultant = $"c-{Guid.NewGuid():N}@example.com";
        (await admin.PostAsJsonAsync("/api/identity/staff", new { email = consultant, displayName = "Tư vấn", initialPassword = Password, roles = new[] { "Consultant" } }, Ct))
            .EnsureSuccessStatusCode();

        (await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/crm/public/leads", new
        {
            studentName = "Khách Mới",
            phone = "0901234567",
            languageCode = "en",
            consentVersion = "2026-11",
            consentGiven = true,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _api.DrainOutboxAsync();
        await SendAllAsync();

        var message = _mail.To(consultant).Single();
        message.Subject.ShouldStartWith("Lead mới: Khách Mới");
        message.Text.ShouldContain("/ops/leads");
        _mail.To(email).ShouldBeEmpty(); // assigned: the admin is not bothered
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task SendAllAsync()
    {
        var dispatcher = _api.Services.GetRequiredService<NotificationDispatcher>();
        for (var round = 0; round < 10 && await dispatcher.ProcessDueAsync(200, Ct) > 0; round++)
        {
        }
    }

    private async Task<ClassSetup> SetUpClassAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("ntf", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<Guid> Post(string path, object body, string key = "id")
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return Guid.Parse((await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))![key].ToString()!);
        }

        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var teacher = await Post("/api/identity/staff", new { email = teacherEmail, displayName = "GV Thông báo", initialPassword = Password, roles = new[] { "Teacher" } }, "userId");
        var branch = await Post("/api/organization/branches", new { code = "Q1", name = new { vi = "Q1" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false });
        var program = await Post("/api/academic/programs", new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false });
        var level = await Post($"/api/academic/programs/{program}/levels", new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 2, sessionMinutes = 90 });
        var code = $"N-{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var cls = await Post("/api/academic/classes", new { code, courseLevelId = level, branchId = branch, deliveryMode = "Offline", capacity = 10, startsOn = "2027-03-01" });

        var englishEmail = $"en-{Guid.NewGuid():N}@example.com";
        var vietnameseEmail = $"vi-{Guid.NewGuid():N}@example.com";
        var english = await Post("/api/crm/students", new { fullName = "Anna Student", email = englishEmail, communicationLocale = "en" });
        var vietnamese = await Post("/api/crm/students", new { fullName = "Học Viên Việt", email = vietnameseEmail, communicationLocale = "vi" });
        var silent = await Post("/api/crm/students", new { fullName = "Không Email" });
        foreach (var student in new[] { english, vietnamese, silent })
        {
            await Post($"/api/academic/classes/{cls}/enrollments", new { studentId = student, type = "Regular" }, "enrollmentId");
        }

        (await admin.PostAsJsonAsync($"/api/scheduling/classes/{cls}/schedule",
            new { daysOfWeek = new[] { 2 }, startTime = "19:00", teacherId = teacher }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await _api.DrainOutboxAsync();
        return new ClassSetup(tenant.Id, tenant.Name, admin, cls, code, teacherEmail, englishEmail, vietnameseEmail);
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed record ClassSetup(
        Guid TenantId, string TenantName, HttpClient Admin, Guid ClassId, string Code, string TeacherEmail, string EnglishEmail, string VietnameseEmail);

    private sealed record SessionView(Guid Id, string Status);

    private sealed record MessageView(
        Guid Id, string TemplateKey, string RecipientAddress, string Locale, string Status, int Attempts, string? LastError, string? Subject);
}

/// <summary>Records what would be sent; can fail chosen addresses a number of times.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private readonly ConcurrentDictionary<string, (DeliveryResult Result, int Times)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public void Fail(string address, DeliveryResult result, int times) => _failures[address] = (result, times);

    public IReadOnlyList<EmailMessage> To(string address) =>
        _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (_failures.TryGetValue(message.To, out var failure) && failure.Times > 0)
        {
            _failures[message.To] = (failure.Result, failure.Times - 1);
            return Task.FromResult(failure.Result);
        }

        _sent.Enqueue(message);
        return Task.FromResult(DeliveryResult.Ok);
    }
}
