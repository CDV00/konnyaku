using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Konnyaku.Notification.Application;
using Konnyaku.Notification.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// In-app inbox and channel router (doc 10.9): events become inbox items of the person's account, signals go to the
/// realtime publisher after the commit, email and Web Push follow the person's choices, emails of one kind are grouped.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InboxTests : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly PostgresFixture _db;
    private readonly FakeEmailSender _mail = new();
    private readonly FakeRealtime _realtime = new();
    private readonly FakePush _push = new();
    private readonly KonnyakuApiFactory _api;

    public InboxTests(PostgresFixture db)
    {
        _db = db;
        _api = new KonnyakuApiFactory(db)
        {
            TestServices = services =>
            {
                services.AddSingleton<IEmailSender>(_mail);
                services.AddSingleton<IRealtimePublisher>(_realtime);
                services.AddSingleton<IPushSender>(_push);
            },
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task A_session_change_reaches_the_inbox_signals_the_tabs_and_read_state_follows()
    {
        var c = await SetUpAsync("inbox");
        await c.RescheduleFirstAsync(_api);

        var page = await c.Learner.GetFromJsonAsync<InboxPageView>("/api/notification/inbox?unread=true", Ct);
        var item = page!.Items.ShouldHaveSingleItem();
        item.Kind.ShouldBe("session.rescheduled");
        item.TitleKey.ShouldBe("session.rescheduled");
        item.Link.ShouldBe("/learn/schedule");
        item.Params["classCode"].ShouldBe(c.Code);
        item.Params["sequenceNo"].ShouldBe("1");
        DateTimeOffset.Parse(item.Params["startsAt"], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(c.NewStart);
        item.ReadAt.ShouldBeNull();
        page.Unread.ShouldBe(1);
        page.Cursor.ShouldBe(item.Seq);

        // Signal after the commit: type, id and sequence only.
        var signal = _realtime.For(c.LearnerUserId).ShouldHaveSingleItem();
        signal.ShouldBe(new RealtimeSignal("inbox", item.Id, item.Seq));
        var teacherItem = (await c.TeacherClient.GetFromJsonAsync<InboxPageView>("/api/notification/inbox?unread=true", Ct))!.Items.ShouldHaveSingleItem();
        teacherItem.Link.ShouldBe("/teach");

        // Reconnect: sync from a cursor returns what came after it, nothing twice.
        var sync = (await c.Learner.GetFromJsonAsync<InboxSyncView>($"/api/notification/inbox/sync?since={item.Seq - 1}", Ct))!;
        sync.Items.Select(i => i.Id).ShouldBe([item.Id]);
        sync.Cursor.ShouldBe(item.Seq);
        (await c.Learner.GetFromJsonAsync<InboxSyncView>($"/api/notification/inbox/sync?since={sync.Cursor}", Ct))!.Items.ShouldBeEmpty();

        // The outbox delivers at least once: a replayed event adds nothing.
        await ExecAsync($"UPDATE scheduling.outbox_message SET processed_at = NULL WHERE tenant_id = '{c.TenantId}' AND type LIKE '%SessionRescheduledV1%'");
        await _api.DrainOutboxAsync();
        (await c.Learner.GetFromJsonAsync<InboxCountView>("/api/notification/inbox/unread-count", Ct))!.Unread.ShouldBe(1);

        // Someone else cannot read it; the owner can, and the other tabs hear of it.
        await (await c.TeacherClient.PostAsync($"/api/notification/inbox/{item.Id}/read", null, Ct))
            .ShouldFailWith(HttpStatusCode.NotFound, "notification.inbox_item_not_found");
        var read = await c.Learner.PostAsync($"/api/notification/inbox/{item.Id}/read", null, Ct);
        (await read.Content.ReadFromJsonAsync<InboxCountView>(Ct))!.Unread.ShouldBe(0);
        _realtime.For(c.LearnerUserId).Last().Type.ShouldBe("inbox.read");
        (await c.Learner.GetFromJsonAsync<InboxPageView>("/api/notification/inbox?unread=true", Ct))!.Items.ShouldBeEmpty();

        var all = await c.TeacherClient.PostAsync("/api/notification/inbox/read-all", null, Ct);
        (await all.Content.ReadFromJsonAsync<InboxCountView>(Ct))!.Unread.ShouldBe(0);

        // Another tenant never sees these rows, even through SQL as the module role (RLS).
        var other = await _api.CreateTenantWithAdminAsync("inboxo", $"a-{Guid.NewGuid():N}@example.com", Password);
        (await CountAsTenantAsync(other.Id, "notification.inbox_item")).ShouldBe(0);
        // The learner: enrollment (from the set-up) and the change; the teacher: the change.
        (await CountAsTenantAsync(c.TenantId, "notification.inbox_item")).ShouldBe(3);
    }

    [Fact]
    public async Task Turning_email_off_keeps_the_inbox_and_each_area_lists_its_own_kinds()
    {
        var c = await SetUpAsync("prefs");
        await SendAllAsync();

        var learnerKinds = (await c.Learner.GetFromJsonAsync<PreferenceView[]>("/api/notification/preferences", Ct))!;
        learnerKinds.Select(p => p.Kind).ShouldContain("assessment.graded");
        learnerKinds.Select(p => p.Kind).ShouldNotContain("lead.created");
        learnerKinds.Single(p => p.Kind == "assessment.graded").EmailAvailable.ShouldBeFalse();
        learnerKinds.ShouldAllBe(p => p.Push && (p.Email || !p.EmailAvailable));
        var staffKinds = (await c.TeacherClient.GetFromJsonAsync<PreferenceView[]>("/api/notification/preferences", Ct))!;
        staffKinds.Select(p => p.Kind).ShouldContain("lead.created");
        staffKinds.Select(p => p.Kind).ShouldNotContain("assessment.graded");

        await (await c.Learner.PutAsJsonAsync("/api/notification/preferences", new[] { new { kind = "lead.created", email = false, push = false } }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "notification.preferences_invalid");
        var saved = await c.Learner.PutAsJsonAsync("/api/notification/preferences",
            new[] { new { kind = "session.rescheduled", email = false, push = true } }, Ct);
        await saved.ShouldSucceed();
        (await saved.Content.ReadFromJsonAsync<PreferenceView[]>(Ct))!.Single(p => p.Kind == "session.rescheduled").Email.ShouldBeFalse();

        await c.RescheduleFirstAsync(_api);
        await SendAllAsync();
        (await c.Learner.GetFromJsonAsync<InboxCountView>("/api/notification/inbox/unread-count", Ct))!.Unread.ShouldBe(1);
        _mail.To(c.LearnerEmail).ShouldNotContain(m => m.Subject.Contains("moved") || m.Subject.StartsWith("Đổi giờ", StringComparison.Ordinal));
        _mail.To(c.TeacherEmail).Count(m => m.Subject.StartsWith("Đổi giờ", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Emails_of_one_kind_within_the_window_go_out_as_one_digest()
    {
        var c = await SetUpAsync("digest");
        await SendAllAsync();
        var sessions = await c.SessionsAsync();

        await (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[0].Id}/cancel", new { reason = "Lý do 1" }, Ct)).ShouldSucceed();
        await _api.DrainOutboxAsync();
        await SendAllAsync();
        _mail.To(c.TeacherEmail).Count(m => m.Subject.StartsWith("Hủy buổi", StringComparison.Ordinal)).ShouldBe(1);

        // Two more within ten minutes: held, not sent one by one.
        foreach (var session in sessions[1..3])
        {
            await (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{session.Id}/cancel", new { reason = "Lý do" }, Ct)).ShouldSucceed();
        }

        await _api.DrainOutboxAsync();
        await SendAllAsync();
        _mail.To(c.TeacherEmail).Count.ShouldBe(1);
        (await c.TeacherClient.GetFromJsonAsync<InboxCountView>("/api/notification/inbox/unread-count", Ct))!.Unread.ShouldBe(3);

        // The window ends: one email listing both.
        await ExecAsync($"UPDATE notification.notification_message SET sent_at = sent_at - interval '11 minutes' WHERE tenant_id = '{c.TenantId}' AND status = 'Sent'");
        await ExecAsync($"UPDATE notification.dispatch_item SET due_at = now() WHERE tenant_id = '{c.TenantId}' AND kind = 'send'");
        await SendAllAsync();
        var mails = _mail.To(c.TeacherEmail);
        mails.Count.ShouldBe(2);
        mails[1].Subject.ShouldBe("2 thông báo mới");
        mails[1].Text.ShouldContain($"Hủy buổi 2 lớp {c.Code}");
        mails[1].Text.ShouldContain($"Hủy buổi 3 lớp {c.Code}");
        mails[1].Text.ShouldContain("/teach/inbox");
        var log = (await c.Admin.GetFromJsonAsync<MessageView[]>("/api/notification/messages", Ct))!;
        log.Where(m => m.RecipientAddress == c.TeacherEmail && m.TemplateKey == "session.cancelled").ShouldAllBe(m => m.Status == "Sent");
    }

    [Fact]
    public async Task Push_reaches_every_browser_and_gone_or_signed_out_browsers_are_deleted()
    {
        var c = await SetUpAsync("push");
        (await c.Learner.GetFromJsonAsync<PushConfigView>("/api/notification/push/config", Ct))!.ShouldBe(new PushConfigView(true, FakePush.Key));
        const string Phone = "https://push.example.com/send/phone";
        const string Laptop = "https://push.example.com/send/laptop";
        foreach (var endpoint in new[] { Phone, Laptop })
        {
            await (await c.Learner.PostAsJsonAsync("/api/notification/push/subscriptions",
                new { endpoint, keys = new { p256dh = "BPub", auth = "secret" } }, Ct)).ShouldSucceed();
        }

        await (await c.Learner.PostAsJsonAsync("/api/notification/push/subscriptions",
            new { endpoint = "http://insecure.example.com/x", keys = new { p256dh = "BPub", auth = "secret" } }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "notification.push_subscription_invalid");
        _push.Gone.Add(Laptop);

        await c.RescheduleFirstAsync(_api);
        await SendAllAsync();
        _push.Sent.Order().ShouldBe([Laptop, Phone]);
        (await CountAsTenantAsync(c.TenantId, "notification.push_subscription")).ShouldBe(1);

        // Signing out of this browser deletes its subscription; push off means no push at all.
        (await c.Learner.DeleteAsync($"/api/notification/push/subscriptions?endpoint={Uri.EscapeDataString(Phone)}", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CountAsTenantAsync(c.TenantId, "notification.push_subscription")).ShouldBe(0);
        await (await c.Learner.PostAsJsonAsync("/api/notification/push/subscriptions",
            new { endpoint = Phone, keys = new { p256dh = "BPub", auth = "secret" } }, Ct)).ShouldSucceed();
        await (await c.Learner.PutAsJsonAsync("/api/notification/preferences",
            new[] { new { kind = "session.cancelled", email = true, push = false } }, Ct)).ShouldSucceed();
        var sessions = await c.SessionsAsync();
        await (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[1].Id}/cancel", new { reason = "x" }, Ct)).ShouldSucceed();
        await _api.DrainOutboxAsync();
        await SendAllAsync();
        _push.Sent.Count.ShouldBe(2);
        (await c.Learner.GetFromJsonAsync<InboxCountView>("/api/notification/inbox/unread-count", Ct))!.Unread.ShouldBe(2);
    }

    [Fact]
    public async Task Realtime_tickets_are_short_lived_and_signed_for_the_realtime_service()
    {
        var c = await SetUpAsync("ticket");
        var response = await c.Learner.PostAsync("/api/notification/realtime/ticket", null, Ct);
        await response.ShouldSucceed();
        var view = (await response.Content.ReadFromJsonAsync<TicketView>(Ct))!;
        view.Url.ShouldBe("/rt");
        (view.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(61));

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(view.Ticket, new TokenValidationParameters
        {
            ValidIssuer = "konnyaku",
            ValidAudience = "konnyaku-realtime",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(KonnyakuApiFactory.RtcSecret)),
        });
        validated.IsValid.ShouldBeTrue();
        validated.Claims["sub"].ShouldBe(c.LearnerUserId.ToString());
        validated.Claims["tid"].ShouldBe(c.TenantId.ToString());
        (await _api.ClientFor(c.Slug).PostAsync("/api/notification/realtime/ticket", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_redis_publisher_sends_type_id_and_sequence_on_the_channel_of_the_user()
    {
        await using var redis = new RedisBuilder("redis:8.2-alpine").Build();
        await redis.StartAsync(Ct);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var received = new TaskCompletionSource<(string Channel, string Payload)>();
        var user = Guid.CreateVersion7();
        await connection.GetSubscriber().SubscribeAsync(RedisChannel.Pattern("user:*"),
            (channel, value) => received.TrySetResult((channel.ToString(), value.ToString())));

        var item = Guid.CreateVersion7();
        await new RedisRealtimePublisher(connection, NullLogger<RedisRealtimePublisher>.Instance)
            .PublishAsync(user, new RealtimeSignal("inbox", item, 812), Ct);

        var (channel, payload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        channel.ShouldBe($"user:{user}");
        payload.ShouldBe($$"""{"t":"inbox","id":"{{item}}","seq":812}""");
    }

    [Fact]
    public void Vapid_keys_from_gen_secrets_load_and_sign_es256_tokens()
    {
        // A pair printed by infra/deploy/scripts/gen-secrets.sh (also the dev pair of appsettings.Development.json).
        using var sender = new VapidPushSender(new PushOptions
        {
            PublicKey = "BNBlnEjDEnj-BIYhRTOrCkO-WB0J4RNtcCI2qtMn1iJfa09jhvChrurFgy9QjDr5DASGZgmLM2zrJdAVT05dDx4",
            PrivateKey = "glc2o1tqKZ0FVmN69JKsshE5wL4UKrKdVx7eczEjAVg",
            Subject = "mailto:ops@example.com",
        }, NullLogger<VapidPushSender>.Instance);
        sender.IsConfigured.ShouldBeTrue();
        var token = new JsonWebToken(sender.Token(new Uri("https://fcm.googleapis.com/fcm/send/abc")));
        token.Alg.ShouldBe("ES256");
        token.Audiences.ShouldBe(["https://fcm.googleapis.com"]);
        token.GetClaim("sub").Value.ShouldBe("mailto:ops@example.com");
        VapidPushSender.Classify(HttpStatusCode.Gone).ShouldBe(PushOutcome.Gone);
        VapidPushSender.Classify(HttpStatusCode.Created).ShouldBe(PushOutcome.Delivered);
        VapidPushSender.Classify(HttpStatusCode.TooManyRequests).ShouldBe(PushOutcome.Transient);

        using var broken = new VapidPushSender(new PushOptions { PublicKey = "BNBl", PrivateKey = "x" }, NullLogger<VapidPushSender>.Instance);
        broken.IsConfigured.ShouldBeFalse();
    }

    private async Task SendAllAsync()
    {
        var dispatcher = _api.Services.GetRequiredService<NotificationDispatcher>();
        for (var round = 0; round < 10 && await dispatcher.ProcessDueAsync(200, Ct) > 0; round++)
        {
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Rows visible to the notification role with a tenant set, like the app sees them.</summary>
    private async Task<long> CountAsTenantAsync(Guid tenantId, string table)
    {
        await using var connection = new NpgsqlConnection(_db.ModuleConnectionString("notification"));
        await connection.OpenAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantId}', false)", connection))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<Setup> SetUpAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<JsonElement> Post(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var teacher = (await Post("/api/identity/staff", new { email = teacherEmail, displayName = "GV Hộp thư", initialPassword = Password, roles = new[] { "Teacher" } }))
            .GetProperty("userId").GetGuid();
        var branch = (await Post("/api/organization/branches", new { code = "Q1", name = new { vi = "Q1" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false })).GetProperty("id").GetGuid();
        var program = (await Post("/api/academic/programs", new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false }))
            .GetProperty("id").GetGuid();
        var level = (await Post($"/api/academic/programs/{program}/levels", new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 4, sessionMinutes = 90 }))
            .GetProperty("id").GetGuid();
        var code = $"I-{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var cls = (await Post("/api/academic/classes", new { code, courseLevelId = level, branchId = branch, deliveryMode = "Offline", capacity = 10, startsOn = "2027-03-01" }))
            .GetProperty("id").GetGuid();

        var account = await Post("/api/identity/learners", new { displayName = "Học Viên Hộp Thư", role = "Student" });
        var learnerUserId = account.GetProperty("userId").GetGuid();
        var learnerEmail = $"hv-{Guid.NewGuid():N}@example.com";
        var student = (await Post("/api/crm/students", new { fullName = "Học Viên Hộp Thư", email = learnerEmail, communicationLocale = "vi", userId = learnerUserId }))
            .GetProperty("id").GetGuid();
        await Post($"/api/academic/classes/{cls}/enrollments", new { studentId = student, type = "Regular" });
        (await admin.PostAsJsonAsync($"/api/scheduling/classes/{cls}/schedule", new { daysOfWeek = new[] { 2 }, startTime = "19:00", teacherId = teacher }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await _api.DrainOutboxAsync();

        var learnerToken = (await (await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account.GetProperty("username").GetString(), password = account.GetProperty("temporaryPassword").GetString() }, Ct))
            .Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
        var teacherToken = (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken;
        var setup = new Setup(tenant.Id, tenant.Slug, admin, cls, code, learnerUserId, learnerEmail, _api.ClientFor(tenant.Slug, learnerToken), teacherEmail,
            _api.ClientFor(tenant.Slug, teacherToken));

        // A clean start: the enrollment notice of the learner is read, no signal recorded yet.
        var welcome = (await setup.Learner.GetFromJsonAsync<InboxPageView>("/api/notification/inbox", Ct))!.Items.ShouldHaveSingleItem();
        welcome.Kind.ShouldBe("enrollment.activated");
        welcome.Params["classCode"].ShouldBe(code);
        await (await setup.Learner.PostAsync("/api/notification/inbox/read-all", null, Ct)).ShouldSucceed();
        _realtime.Clear();
        return setup;
    }

    private sealed record Setup(
        Guid TenantId, string Slug, HttpClient Admin, Guid ClassId, string Code, Guid LearnerUserId, string LearnerEmail, HttpClient Learner, string TeacherEmail,
        HttpClient TeacherClient)
    {
        public DateTimeOffset NewStart { get; } = new(2027, 3, 5, 11, 0, 0, TimeSpan.Zero);

        public async Task<SessionRow[]> SessionsAsync() =>
            (await Admin.GetFromJsonAsync<SessionRow[]>($"/api/scheduling/classes/{ClassId}/sessions", Ct))!.OrderBy(s => s.SequenceNo).ToArray();

        /// <summary>Moves session 1 to 5 March 2027 18:00 in Ho Chi Minh City (11:00 UTC) and runs the outbox.</summary>
        public async Task RescheduleFirstAsync(KonnyakuApiFactory api)
        {
            var sessions = await SessionsAsync();
            await (await Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[0].Id}/reschedule", new { date = "2027-03-05", startTime = "18:00" }, Ct))
                .ShouldSucceed();
            await api.DrainOutboxAsync();
        }
    }

    private sealed record SessionRow(Guid Id, int SequenceNo);

    private sealed record InboxItemRow(
        Guid Id, string Kind, string TitleKey, Dictionary<string, string> Params, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, long Seq);

    private sealed record InboxPageView(InboxItemRow[] Items, int Total, int Unread, long Cursor);

    private sealed record InboxSyncView(InboxItemRow[] Items, int Unread, long Cursor, bool More);

    private sealed record InboxCountView(int Unread, long Cursor);

    private sealed record PreferenceView(string Kind, bool Email, bool Push, bool EmailAvailable);

    private sealed record PushConfigView(bool Enabled, string? PublicKey);

    private sealed record TicketView(string Ticket, DateTimeOffset ExpiresAt, string Url);

    private sealed record MessageView(string TemplateKey, string RecipientAddress, string Status);
}

/// <summary>Records realtime signals per user (what the Redis publisher would send).</summary>
public sealed class FakeRealtime : IRealtimePublisher
{
    private readonly ConcurrentQueue<(Guid User, RealtimeSignal Signal)> _signals = new();

    public IReadOnlyList<RealtimeSignal> For(Guid userId) => _signals.Where(s => s.User == userId).Select(s => s.Signal).ToList();

    public void Clear() => _signals.Clear();

    public Task PublishAsync(Guid userId, RealtimeSignal signal, CancellationToken cancellationToken)
    {
        _signals.Enqueue((userId, signal));
        return Task.CompletedTask;
    }
}

/// <summary>A configured push sender that records endpoints; endpoints in <see cref="Gone"/> answer 410.</summary>
public sealed class FakePush : IPushSender
{
    public const string Key = "BTestPublicKey";

    public ConcurrentBag<string> Gone { get; } = [];

    public ConcurrentQueue<string> Sent { get; } = new();

    public bool IsConfigured => true;

    public string? PublicKey => Key;

    public Task<PushOutcome> SendAsync(PushTarget target, CancellationToken cancellationToken)
    {
        Sent.Enqueue(target.Endpoint);
        return Task.FromResult(Gone.Contains(target.Endpoint) ? PushOutcome.Gone : PushOutcome.Delivered);
    }
}
