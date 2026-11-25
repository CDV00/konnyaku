using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>Session seats and bookings: leave and make-ups, deadlines, hybrid seats, flexible classes and the waitlist.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BookingTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private const string TimeZone = "Asia/Ho_Chi_Minh";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Leave_in_time_gives_a_makeup_in_the_same_lesson_of_another_class()
    {
        var center = await CenterAsync("mkp");
        var level = await center.LevelAsync(3);
        var home = await center.ClassAsync("HOME", level, "Online", 5);
        var other = await center.ClassAsync("OTHER", level, "Online", 5);
        var learner = await center.LearnerAsync("An");
        await center.EnrollAsync(home, learner);
        var homeSessions = await center.ScheduleAsync(home, 2, "19:00");
        var otherSessions = await center.ScheduleAsync(other, 3, "18:00");
        await _api.DrainOutboxAsync();

        var bookings = await learner.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct);
        bookings!.Length.ShouldBe(3);
        bookings.ShouldAllBe(b => b.Kind == "Regular" && b.Status == "Booked" && b.Mode == "Online" && b.CanPostpone);

        var postponed = await learner.Client.PostAsync($"/api/scheduling/my/bookings/{bookings[0].Id}/postpone", null, Ct);
        postponed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entitlementId = (await postponed.Content.ReadFromJsonAsync<PostponeView>(Ct))!.EntitlementId;
        (await learner.Client.GetFromJsonAsync<SessionView[]>(Window("/api/scheduling/my/sessions"), Ct))!
            .Select(s => s.Id).ShouldNotContain(homeSessions[0].Id);

        // Only the session of the same lesson (the first) in the other class is offered.
        var options = (await learner.Client.GetFromJsonAsync<OptionView[]>($"/api/scheduling/my/makeups/{entitlementId}/options", Ct))!;
        options.Select(o => o.SessionId).ShouldBe([otherSessions[0].Id]);
        (await ErrorOf(await learner.Client.PostAsJsonAsync($"/api/scheduling/my/makeups/{entitlementId}/book",
            new { sessionId = otherSessions[1].Id }, Ct))).ShouldBe("scheduling.makeup_lesson_mismatch");
        (await learner.Client.PostAsJsonAsync($"/api/scheduling/my/makeups/{entitlementId}/book",
            new { sessionId = otherSessions[0].Id }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ErrorOf(await learner.Client.PostAsJsonAsync($"/api/scheduling/my/makeups/{entitlementId}/book",
            new { sessionId = otherSessions[0].Id }, Ct))).ShouldBe("scheduling.entitlement_used");

        // The postponed session has no attendance row; the host session lists the make-up learner, recorded as Makeup.
        (await center.Admin.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{homeSessions[0].Id}/attendance", Ct))!.ShouldBeEmpty();
        var roster = (await center.Admin.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{otherSessions[0].Id}/attendance", Ct))!;
        var guest = roster.ShouldHaveSingleItem();
        guest.Kind.ShouldBe("Makeup");
        guest.Mode.ShouldBe("Online");
        var confirmed = await center.Admin.PostAsJsonAsync($"/api/learning/sessions/{otherSessions[0].Id}/attendance/confirm",
            new { overrides = new[] { new { enrollmentId = guest.EnrollmentId, status = "Present" } } }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await confirmed.Content.ReadFromJsonAsync<AttendanceView[]>(Ct))!.Single().Status.ShouldBe("Makeup");
        await _api.DrainOutboxAsync();

        var after = (await learner.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!;
        after.Single(b => b.SessionId == homeSessions[0].Id).Status.ShouldBe("Postponed");
        after.Single(b => b.SessionId == otherSessions[0].Id).Status.ShouldBe("Attended");
        var makeups = (await learner.Client.GetFromJsonAsync<EntitlementView[]>("/api/scheduling/my/makeups", Ct))!;
        makeups.Single().State.ShouldBe("Used");

        // Another learner's account cannot touch these bookings.
        var stranger = await center.LearnerAsync("Lạ");
        (await stranger.Client.PostAsync($"/api/scheduling/my/bookings/{bookings[1].Id}/postpone", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Leave_is_refused_after_the_deadline_and_beyond_the_limit()
    {
        var center = await CenterAsync("ddl");
        var level = await center.LevelAsync(3);
        var cls = await center.ClassAsync("DDL", level, "Online", 5);
        var learner = await center.LearnerAsync("Bình");
        await center.EnrollAsync(cls, learner);
        var sessions = await center.ScheduleAsync(cls, 2, "19:00");
        await _api.DrainOutboxAsync();
        (await center.Admin.PutAsJsonAsync("/api/scheduling/settings/booking", new
        {
            postponeDeadlineHours = 12, maxPostponesPerEnrollment = 1, entitlementDays = 30, modeSwitchDeadlineHours = 2,
            makeupSeatsPerSession = 2, waitlistHoldHours = 2,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The first session moves to three hours from now: inside the 12-hour deadline.
        var soon = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(3), TimeZoneInfo.FindSystemTimeZoneById(TimeZone));
        (await center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[0].Id}/reschedule",
            new { date = DateOnly.FromDateTime(soon.DateTime), startTime = TimeOnly.FromDateTime(soon.DateTime).ToString("HH:mm") }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var bookings = (await learner.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings", DateTimeOffset.UtcNow.AddDays(-1)), Ct))!;
        var first = bookings.Single(b => b.SessionId == sessions[0].Id);
        first.CanPostpone.ShouldBeFalse();
        (await ErrorOf(await learner.Client.PostAsync($"/api/scheduling/my/bookings/{first.Id}/postpone", null, Ct)))
            .ShouldBe("scheduling.postpone_deadline_passed");

        (await learner.Client.PostAsync($"/api/scheduling/my/bookings/{bookings.Single(b => b.SessionId == sessions[1].Id).Id}/postpone", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ErrorOf(await learner.Client.PostAsync($"/api/scheduling/my/bookings/{bookings.Single(b => b.SessionId == sessions[2].Id).Id}/postpone", null, Ct)))
            .ShouldBe("scheduling.postpone_limit_reached");

        // Staff see the learner among those who postpone (at least once here).
        var frequent = (await center.Admin.GetFromJsonAsync<FrequentView[]>("/api/scheduling/postpones/frequent?min=1", Ct))!;
        frequent.ShouldHaveSingleItem().Postponed.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_learners_take_the_last_makeup_seat_once()
    {
        var center = await CenterAsync("last");
        var level = await center.LevelAsync(2);
        var home = await center.ClassAsync("HOME", level, "Online", 10);
        var other = await center.ClassAsync("HOST", level, "Online", 10);
        (await center.Admin.PutAsJsonAsync("/api/scheduling/settings/booking", new
        {
            postponeDeadlineHours = 12, maxPostponesPerEnrollment = 3, entitlementDays = 30, modeSwitchDeadlineHours = 2,
            makeupSeatsPerSession = 1, waitlistHoldHours = 2,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var learners = new List<Learner>();
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
        {
            var learner = await center.LearnerAsync(name);
            await center.EnrollAsync(home, learner);
            learners.Add(learner);
        }

        var homeSessions = await center.ScheduleAsync(home, 2, "19:00");
        var hostSessions = await center.ScheduleAsync(other, 4, "19:00");
        await _api.DrainOutboxAsync();

        var entitlements = new List<(Learner Learner, Guid Entitlement)>();
        foreach (var learner in learners)
        {
            var booking = (await learner.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!
                .Single(b => b.SessionId == homeSessions[0].Id);
            var postponed = await learner.Client.PostAsync($"/api/scheduling/my/bookings/{booking.Id}/postpone", null, Ct);
            entitlements.Add((learner, (await postponed.Content.ReadFromJsonAsync<PostponeView>(Ct))!.EntitlementId));
        }

        var results = await Task.WhenAll(entitlements.Select(e =>
            e.Learner.Client.PostAsJsonAsync($"/api/scheduling/my/makeups/{e.Entitlement}/book", new { sessionId = hostSessions[0].Id }, Ct)));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var refused in results.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            (await ErrorOf(refused)).ShouldBe("scheduling.session_full");
        }

        var roster = (await center.Admin.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{hostSessions[0].Id}/attendance", Ct))!;
        roster.Count(r => r.Kind == "Makeup").ShouldBe(1);

        // A refused learner waits; when the make-up learner cancels in time, the Worker offers them the seat.
        var (waiting, waitingEntitlement) = entitlements.First(e => results[entitlements.IndexOf(e)].StatusCode != HttpStatusCode.OK);
        (await waiting.Client.PostAsJsonAsync($"/api/scheduling/my/sessions/{hostSessions[0].Id}/waitlist",
            new { entitlementId = waitingEntitlement }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var winner = entitlements[Array.FindIndex(results, r => r.StatusCode == HttpStatusCode.OK)].Learner;
        var makeup = (await winner.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!.Single(b => b.Kind == "Makeup");
        (await winner.Client.PostAsync($"/api/scheduling/my/bookings/{makeup.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _api.DrainOutboxAsync();

        var offer = (await waiting.Client.GetFromJsonAsync<WaitlistView[]>("/api/scheduling/my/waitlist", Ct))!.ShouldHaveSingleItem();
        offer.Status.ShouldBe("Offered");
        offer.OfferExpiresAt.ShouldNotBeNull();
        (await waiting.Client.PostAsync($"/api/scheduling/my/waitlist/{offer.Id}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await waiting.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!
            .ShouldContain(b => b.SessionId == hostSessions[0].Id && b.Kind == "Makeup" && b.Status == "Booked");
        (await winner.Client.GetFromJsonAsync<EntitlementView[]>("/api/scheduling/my/makeups", Ct))!.Single().State.ShouldBe("Open");
    }

    [Fact]
    public async Task Hybrid_seats_follow_the_room_and_switch_while_the_other_mode_has_room()
    {
        var center = await CenterAsync("hyb");
        var room = await center.RoomAsync("R1", 1);
        var level = await center.LevelAsync(2);
        var cls = await center.ClassAsync("HYB", level, "Hybrid", 3);
        var first = await center.LearnerAsync("Chi");
        var second = await center.LearnerAsync("Dũng");
        await center.EnrollAsync(cls, first, "Offline");
        await center.EnrollAsync(cls, second, "Offline");
        var sessions = await center.ScheduleAsync(cls, 4, "19:00", room);
        await _api.DrainOutboxAsync();

        // One classroom seat: the first learner sits there, the second goes online.
        var a = (await first.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!.First(b => b.SessionId == sessions[0].Id);
        var b = (await second.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!.First(x => x.SessionId == sessions[0].Id);
        a.Mode.ShouldBe("Offline");
        b.Mode.ShouldBe("Online");
        (await ErrorOf(await second.Client.PostAsJsonAsync($"/api/scheduling/my/bookings/{b.Id}/mode", new { mode = "Offline" }, Ct)))
            .ShouldBe("scheduling.mode_full");
        (await first.Client.PostAsJsonAsync($"/api/scheduling/my/bookings/{a.Id}/mode", new { mode = "Online" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Client.PostAsJsonAsync($"/api/scheduling/my/bookings/{b.Id}/mode", new { mode = "Offline" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var roster = (await center.Admin.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{sessions[0].Id}/attendance", Ct))!;
        roster.Single(r => r.StudentId == first.StudentId).Mode.ShouldBe("Online");
        roster.Single(r => r.StudentId == second.StudentId).Mode.ShouldBe("Offline");

        // Dropping the enrollment frees every coming seat.
        var enrollment = roster.Single(r => r.StudentId == second.StudentId).EnrollmentId;
        (await center.Admin.PostAsync($"/api/academic/enrollments/{enrollment}/drop", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _api.DrainOutboxAsync();
        (await second.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!.ShouldBeEmpty();
        (await center.Admin.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{sessions[0].Id}/attendance", Ct))!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Flexible_class_sessions_are_booked_one_by_one()
    {
        var center = await CenterAsync("flex");
        var room = await center.RoomAsync("CLUB", 1);
        var level = await center.LevelAsync(4);
        var cls = await center.ClassAsync("CLUB", level, "Offline", 2, "Flexible");
        var first = await center.LearnerAsync("Em");
        var second = await center.LearnerAsync("Giang");
        await center.EnrollAsync(cls, first);
        await center.EnrollAsync(cls, second);
        var sessions = await center.ScheduleAsync(cls, 6, "10:00", room);
        await _api.DrainOutboxAsync();

        (await first.Client.GetFromJsonAsync<BookingView[]>(Window("/api/scheduling/my/bookings"), Ct))!.ShouldBeEmpty();
        (await first.Client.PostAsJsonAsync($"/api/scheduling/my/sessions/{sessions[0].Id}/book", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ErrorOf(await second.Client.PostAsJsonAsync($"/api/scheduling/my/sessions/{sessions[0].Id}/book", new { }, Ct)))
            .ShouldBe("scheduling.session_full");
        (await second.Client.PostAsJsonAsync($"/api/scheduling/my/sessions/{sessions[1].Id}/book", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var flexible = (await second.Client.GetFromJsonAsync<FlexibleView>("/api/scheduling/my/flexible", Ct))!;
        flexible.Classes.ShouldHaveSingleItem().BookedAhead.ShouldBe(1);
        flexible.Sessions.Single(s => s.SessionId == sessions[0].Id).OfflineFree.ShouldBe(0);

        // A learner of another class cannot book it.
        var outsider = await center.LearnerAsync("Hà");
        (await outsider.Client.PostAsJsonAsync($"/api/scheduling/my/sessions/{sessions[2].Id}/book", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string Window(string path, DateTimeOffset? from = null) =>
        $"{path}?from={Uri.EscapeDataString((from ?? DateTimeOffset.UtcNow).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(400).ToString("O"))}";

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeFalse();
        return (await response.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code;
    }

    private async Task<Center> CenterAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);
        var branch = await Post(admin, "/api/organization/branches", new { code = "CG", name = new { vi = "Cầu Giấy" }, timeZone = TimeZone, isVirtual = false });
        var program = await Post(admin, "/api/academic/programs", new { code = "JP", name = new { vi = "JP" }, languageCode = "ja", frameworkCode = "JLPT", isPublic = false });
        return new Center(_api, tenant.Slug, admin, branch, program);
    }

    private static async Task<Guid> Post(HttpClient client, string path, object body, string key = "id")
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
        var json = (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))!;
        return Guid.Parse(json[key].ToString()!);
    }

    private sealed record Learner(Guid StudentId, HttpClient Client);

    private sealed record Center(KonnyakuApiFactory Api, string Slug, HttpClient Admin, Guid BranchId, Guid ProgramId)
    {
        private int _levels;

        public Task<Guid> LevelAsync(int sessions) => Post(Admin, $"/api/academic/programs/{ProgramId}/levels",
            new { code = $"N{5 - _levels++}", name = new { vi = "Level" }, targetLevelCode = "N5", sessionCount = sessions, sessionMinutes = 90 });

        public Task<Guid> ClassAsync(string code, Guid level, string mode, int capacity, string bookingMode = "Fixed") => Post(Admin, "/api/academic/classes",
            new { code, courseLevelId = level, branchId = BranchId, deliveryMode = mode, capacity, startsOn = StartsOn, bookingMode });

        /// <summary>Soon enough for make-ups (rights last 30 days), late enough for the 12-hour leave deadline.</summary>
        private static DateOnly StartsOn => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        public Task<Guid> RoomAsync(string code, int capacity) => Post(Admin, $"/api/organization/branches/{BranchId}/rooms", new { code, capacity });

        public async Task<Learner> LearnerAsync(string name)
        {
            var response = await Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = name, role = "Student" }, Ct);
            var account = (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))!;
            var student = await Post(Admin, "/api/crm/students", new { fullName = name, userId = account["userId"].ToString() });
            var token = (await (await Api.ClientFor(Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account["username"].ToString(), password = account["temporaryPassword"].ToString() }, Ct))
                .Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
            return new Learner(student, Api.ClientFor(Slug, token));
        }

        public Task<Guid> EnrollAsync(Guid classId, Learner learner, string? mode = null) =>
            Post(Admin, $"/api/academic/classes/{classId}/enrollments", new { studentId = learner.StudentId, type = "Regular", mode }, "enrollmentId");

        public async Task<SessionView[]> ScheduleAsync(Guid classId, int day, string time, Guid? roomId = null)
        {
            var response = await Admin.PostAsJsonAsync($"/api/scheduling/classes/{classId}/schedule",
                new { daysOfWeek = new[] { day }, startTime = time, roomId }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<SessionView[]>(Ct))!.OrderBy(s => s.StartsAt).ToArray();
        }
    }

    private sealed record SessionView(Guid Id, DateTimeOffset StartsAt);

    private sealed record BookingView(Guid Id, Guid SessionId, string Mode, string Kind, string Status, bool CanPostpone, bool CanSwitchMode);

    private sealed record PostponeView(Guid BookingId, Guid EntitlementId);

    private sealed record OptionView(Guid SessionId, int MakeupFree);

    private sealed record EntitlementView(Guid Id, string State);

    private sealed record WaitlistView(Guid Id, string Status, DateTimeOffset? OfferExpiresAt);

    private sealed record AttendanceView(Guid EnrollmentId, Guid StudentId, string Status, string Kind, string? Mode);

    private sealed record FrequentView(Guid StudentId, int Postponed);

    private sealed record FlexibleView(FlexibleClass[] Classes, FlexibleSession[] Sessions);

    private sealed record FlexibleClass(Guid ClassId, int BookedAhead);

    private sealed record FlexibleSession(Guid SessionId, int OfflineFree);
}
