using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>Every record created in ops can be corrected or deactivated, under explicit business rules, with a change log.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EditTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Class_edit_keeps_capacity_above_enrollment_and_locks_structure_once_scheduled()
    {
        var c = await EditCenter.CreateAsync(_api, "edcls");
        var classId = await c.CreateClassAsync("CLS-1", c.BranchA, capacity: 3);
        await c.EnrollNewStudentAsync(classId, "A");
        await c.EnrollNewStudentAsync(classId, "B");

        object Change(string code = "CLS-1", int capacity = 3, Guid? branch = null, Guid? level = null, string startsOn = "2027-01-04", string mode = "Online") =>
            new { code, courseLevelId = level ?? c.LevelId, branchId = branch ?? c.BranchA, deliveryMode = mode, capacity, startsOn };

        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}", Change(capacity: 1), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.capacity_below_enrolled");
        var otherLevel = await c.PostIdAsync($"/api/academic/programs/{c.ProgramId}/levels",
            new { code = "A2", name = new { vi = "A2" }, targetLevelCode = "A2", sessionCount = 4, sessionMinutes = 90 });
        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}", Change(level: otherLevel), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.class_level_locked");

        // Before scheduling: branch and start date may change.
        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}", Change(code: "cls-1b", capacity: 8, branch: c.BranchB, startsOn: "2027-01-11"), Ct))
            .ShouldSucceed();
        var edited = await c.ClassAsync(classId);
        (edited.Code, edited.Capacity, edited.BranchId, edited.StartsOn).ShouldBe(("CLS-1B", 8, c.BranchB, "2027-01-11"));

        await c.ScheduleAsync(classId, [1, 3], "18:00", c.TeacherId);
        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}", Change(code: "CLS-1B", capacity: 8, branch: c.BranchA, startsOn: "2027-01-11"), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.class_branch_locked");
        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}", Change(code: "CLS-1B", capacity: 8, branch: c.BranchB, startsOn: "2027-02-01"), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.class_start_locked");

        var history = await c.HistoryAsync("academic", classId);
        var update = history.First(h => h.Action == "Updated");
        update.ActorId.ShouldBe(c.AdminId);
        update.ActorName.ShouldBe("Admin Test");
        update.Changes.ShouldContain(f => f.Field == "capacity" && f.Old == "3" && f.New == "8");
        update.Changes.ShouldContain(f => f.Field == "branchId" && f.Old == c.BranchA.ToString() && f.New == c.BranchB.ToString());
        history.ShouldContain(h => h.Action == "Created");
    }

    [Fact]
    public async Task Delivery_mode_change_and_closing_reach_future_sessions()
    {
        var c = await EditCenter.CreateAsync(_api, "edmode");
        var classId = await c.CreateClassAsync("CLS-M", c.BranchA);
        await c.ScheduleAsync(classId, [2], "08:00", c.TeacherId);

        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}",
            new { code = "CLS-M", courseLevelId = c.LevelId, branchId = c.BranchA, deliveryMode = "Offline", capacity = 10, startsOn = "2027-01-04" }, Ct))
            .ShouldSucceed();
        await _api.DrainOutboxAsync();
        (await c.SessionsAsync(classId)).ShouldAllBe(s => s.DeliveryMode == "Offline");

        await (await c.Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/close", new { status = "Cancelled" }, Ct)).ShouldSucceed();
        await _api.DrainOutboxAsync();
        (await c.SessionsAsync(classId)).ShouldAllBe(s => s.Status == "Cancelled");
        (await c.ClassAsync(classId)).Status.ShouldBe("Cancelled");

        // A closed class is history: no edits until reopened; the teacher is free again.
        await (await c.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}",
            new { code = "CLS-M", courseLevelId = c.LevelId, branchId = c.BranchA, deliveryMode = "Online", capacity = 10, startsOn = "2027-01-04" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.class_closed");
        await (await c.Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/reopen", new { }, Ct)).ShouldSucceed();
        var other = await c.CreateClassAsync("CLS-N", c.BranchA);
        await c.ScheduleAsync(other, [2], "08:00", c.TeacherId);
    }

    [Fact]
    public async Task Replanning_moves_future_sessions_and_respects_other_classes()
    {
        var c = await EditCenter.CreateAsync(_api, "edplan");
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchA);
        var a = await c.ScheduleAsync(classA, [1, 3], "18:00", c.TeacherId);
        await c.ScheduleAsync(classB, [2, 4], "18:00", c.OtherTeacherId);

        // Moving A onto B's evenings with B's teacher clashes: nothing changes.
        await (await c.Admin.PutAsJsonAsync($"/api/scheduling/classes/{classA}/schedule",
                new { daysOfWeek = new[] { 2, 4 }, startTime = "18:30", teacherId = c.OtherTeacherId }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "scheduling.resource_conflict");
        (await c.SessionsAsync(classA)).Select(s => s.StartsAt).ShouldBe(a.Select(s => s.StartsAt));

        // Same days, other teacher, other time: every future session moves, lessons keep their order.
        var replanned = await c.Admin.PutAsJsonAsync($"/api/scheduling/classes/{classA}/schedule",
            new { daysOfWeek = new[] { 2, 4 }, startTime = "08:00", teacherId = c.TeacherId }, Ct);
        await replanned.ShouldSucceed();
        var moved = await c.SessionsAsync(classA);
        moved.Length.ShouldBe(4);
        moved.ShouldAllBe(s => s.TeacherId == c.TeacherId && s.StartsAt.UtcDateTime.Hour == 1);
        moved.OrderBy(s => s.StartsAt).Select(s => s.SequenceNo).ShouldBe([1, 2, 3, 4]);
        moved.Select(s => DateOnly.Parse(s.LocalDate).DayOfWeek).Distinct().Order().ShouldBe([DayOfWeek.Tuesday, DayOfWeek.Thursday]);

        var history = await c.HistoryAsync("scheduling", classA);
        history.ShouldContain(h => h.EntityType == "SchedulePattern" && h.Changes.Any(f => f.Field == "startTime" && f.Old == "18:00" && f.New == "08:00"));
        history.ShouldContain(h => h.Changes.Any(f => f.Field == "sessionsReplanned" && f.New == "4"));

        // The teacher's old evenings are free again for another class.
        var classC = await c.CreateClassAsync("CLS-C", c.BranchA);
        await c.ScheduleAsync(classC, [1, 3], "18:00", c.TeacherId);
    }

    [Fact]
    public async Task Programs_and_levels_change_within_their_rules()
    {
        var c = await EditCenter.CreateAsync(_api, "edprog");
        await (await c.Admin.PutAsJsonAsync($"/api/academic/programs/{c.ProgramId}",
                new { code = "JP", name = new { vi = "Tiếng Nhật" }, languageCode = "ja", frameworkCode = "JLPT", isPublic = true }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.program_has_levels");
        await (await c.Admin.PutAsJsonAsync($"/api/academic/programs/{c.ProgramId}",
                new { code = "EN-GEN", name = new { vi = "Tiếng Anh tổng quát", en = "General English" }, languageCode = "en", frameworkCode = "CEFR", isPublic = true }, Ct))
            .ShouldSucceed();

        // Unused level: more lessons are fine. Once a class uses it, the lesson count is fixed.
        await (await c.Admin.PutAsJsonAsync($"/api/academic/levels/{c.LevelId}",
            new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 6, sessionMinutes = 60 }, Ct)).ShouldSucceed();
        await c.CreateClassAsync("CLS-P", c.BranchA);
        await (await c.Admin.PutAsJsonAsync($"/api/academic/levels/{c.LevelId}",
                new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 8, sessionMinutes = 60 }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.level_in_use");

        await (await c.Admin.PostAsJsonAsync($"/api/academic/programs/{c.ProgramId}/deactivate", new { }, Ct)).ShouldSucceed();
        var publicPrograms = await _api.ClientFor(c.Slug).GetFromJsonAsync<IdView[]>("/api/academic/public/programs", Ct);
        publicPrograms!.ShouldBeEmpty();
        await (await c.Admin.PostAsJsonAsync("/api/academic/classes",
                new { code = "CLS-Q", courseLevelId = c.LevelId, branchId = c.BranchA, deliveryMode = "Online", capacity = 5, startsOn = "2027-01-04" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "academic.course_level_unavailable");

        var history = await c.HistoryAsync("academic", c.ProgramId);
        history.ShouldContain(h => h.EntityType == "StudyProgram" && h.Changes.Any(f => f.Field == "code" && f.New == "EN-GEN"));
        history.ShouldContain(h => h.EntityType == "CourseLevel" && h.Changes.Any(f => f.Field == "sessionMinutes" && f.Old == "90" && f.New == "60"));
    }

    [Fact]
    public async Task Branches_rooms_and_days_off_are_edited_deactivated_or_deleted()
    {
        var c = await EditCenter.CreateAsync(_api, "edorg");
        await (await c.Admin.PutAsJsonAsync($"/api/organization/branches/{c.BranchB}",
            new { code = "CS-B2", name = new { vi = "Cơ sở Bắc" }, timeZone = "Asia/Bangkok", isVirtual = false }, Ct)).ShouldSucceed();
        await (await c.Admin.PutAsJsonAsync($"/api/organization/branches/{c.BranchB}",
                new { code = "CS-A", name = new { vi = "Trùng" }, timeZone = "Asia/Bangkok", isVirtual = false }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "organization.branch_code_taken");

        var room = await c.PostIdAsync($"/api/organization/branches/{c.BranchA}/rooms", new { code = "P101", capacity = 12 });
        await (await c.Admin.PutAsJsonAsync($"/api/organization/rooms/{room}", new { code = "P102", capacity = 16 }, Ct)).ShouldSucceed();
        await (await c.Admin.PostAsJsonAsync($"/api/organization/rooms/{room}/deactivate", new { }, Ct)).ShouldSucceed();
        var classId = await c.CreateClassAsync("CLS-R", c.BranchA, "Offline");
        await (await c.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classId}/schedule", new { daysOfWeek = new[] { 5 }, startTime = "09:00", roomId = room }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "scheduling.room_invalid");

        await (await c.Admin.PostAsJsonAsync($"/api/organization/branches/{c.BranchB}/deactivate", new { }, Ct)).ShouldSucceed();
        await (await c.Admin.PostAsJsonAsync("/api/academic/classes",
                new { code = "CLS-X", courseLevelId = c.LevelId, branchId = c.BranchB, deliveryMode = "Online", capacity = 5, startsOn = "2027-01-04" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "academic.branch_inactive");

        var holiday = await c.PostIdAsync("/api/organization/holidays", new { date = "2027-04-30", name = new { vi = "Giải phóng" }, branchId = (Guid?)null });
        await (await c.Admin.PutAsJsonAsync($"/api/organization/holidays/{holiday}", new { date = "2027-05-01", name = new { vi = "Quốc tế lao động" }, branchId = c.BranchA }, Ct))
            .ShouldSucceed();
        (await c.Admin.DeleteAsync($"/api/organization/holidays/{holiday}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.Admin.GetFromJsonAsync<IdView[]>("/api/organization/holidays?from=2027-01-01&to=2027-12-31", Ct))!.ShouldBeEmpty();

        (await c.HistoryAsync("organization", holiday)).Select(h => h.Action).ShouldBe(["Deleted", "Updated", "Created"]);
        (await c.HistoryAsync("organization", room)).ShouldContain(h => h.Changes.Any(f => f.Field == "isActive" && f.New == "false"));
        (await c.HistoryAsync("organization", c.BranchB)).ShouldContain(h => h.Changes.Any(f => f.Field == "timeZone" && f.New == "Asia/Bangkok"));
    }

    [Fact]
    public async Task Staff_roles_are_scoped_and_the_last_admin_stays()
    {
        var c = await EditCenter.CreateAsync(_api, "edstaff");
        var (managerId, manager) = await c.AddStaffAsync("Coordinator");

        await (await c.Admin.PutAsJsonAsync($"/api/identity/staff/{managerId}",
                new { displayName = "Quản lý CS-A", roles = new[] { new { role = "Accountant", branchId = (Guid?)c.BranchA } } }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.role_scope_invalid");
        await (await c.Admin.PutAsJsonAsync($"/api/identity/staff/{managerId}",
            new { displayName = "Quản lý CS-A", roles = new[] { new { role = "BranchManager", branchId = (Guid?)c.BranchA } } }, Ct)).ShouldSucceed();

        var staff = (await c.Admin.GetFromJsonAsync<StaffView[]>("/api/identity/staff", Ct))!;
        var edited = staff.Single(s => s.UserId == managerId);
        edited.DisplayName.ShouldBe("Quản lý CS-A");
        edited.Grants.ShouldBe([new GrantView("BranchManager", c.BranchA)]);
        var access = (await manager.GetFromJsonAsync<AccessView[]>("/api/identity/me/access", Ct))!;
        access.Single(a => a.Permission == "academic.classes").ShouldSatisfyAllConditions(
            a => a.TenantWide.ShouldBeFalse(), a => a.BranchIds.ShouldBe([c.BranchA]));

        await (await c.Admin.PutAsJsonAsync($"/api/identity/staff/{c.AdminId}",
                new { displayName = "Admin Test", roles = new[] { new { role = "Teacher", branchId = (Guid?)null } } }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "identity.last_admin");
        await (await c.Admin.PostAsJsonAsync($"/api/identity/staff/{c.AdminId}/deactivate", new { }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "identity.cannot_suspend_self");

        await (await c.Admin.PostAsJsonAsync($"/api/identity/staff/{managerId}/deactivate", new { }, Ct)).ShouldSucceed();
        // Suspended: the live permission check refuses the still-valid token.
        (await manager.PostAsJsonAsync($"/api/organization/branches/{c.BranchA}/rooms", new { code = "P1", capacity = 5 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var history = await c.HistoryAsync("identity", managerId);
        history.ShouldContain(h => h.Changes.Any(f => f.Field == "roles" && f.Old!.Contains("Coordinator") && f.New!.Contains("BranchManager")));
        history.ShouldContain(h => h.Changes.Any(f => f.Field == "displayName" && f.New == "Quản lý CS-A"));
        history.ShouldContain(h => h.Changes.Any(f => f.Field == "status" && f.New == "Suspended"));
    }

    [Fact]
    public async Task Pay_rates_are_corrected_or_removed_until_pay_is_frozen()
    {
        var c = await EditCenter.CreateAsync(_api, "edrate");
        (await c.Admin.PutAsJsonAsync($"/api/hr/teachers/{c.TeacherId}/rate", new { hourlyRate = 200000, currency = "VND", effectiveFrom = "2027-01-01" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var rate = (await c.Admin.GetFromJsonAsync<RateView[]>("/api/hr/rates", Ct))!.Single();

        await (await c.Admin.PutAsJsonAsync($"/api/hr/rates/{rate.Id}", new { hourlyRate = 250000, currency = "VND", effectiveFrom = "2027-02-01" }, Ct)).ShouldSucceed();
        var corrected = (await c.Admin.GetFromJsonAsync<RateView[]>("/api/hr/rates", Ct))!.Single();
        (corrected.HourlyRate, corrected.EffectiveFrom).ShouldBe((250000m, "2027-02-01"));
        (await c.HistoryAsync("hr", c.TeacherId)).ShouldContain(h => h.Changes.Any(f => f.Field == "hourlyRate" && f.Old == "200000" && f.New == "250000"));

        (await c.Admin.DeleteAsync($"/api/hr/rates/{rate.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.Admin.GetFromJsonAsync<RateView[]>("/api/hr/rates", Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Students_and_leads_are_corrected_and_inactive_students_cannot_buy()
    {
        var c = await EditCenter.CreateAsync(_api, "edcrm");
        var studentId = await c.PostIdAsync("/api/crm/students", new { fullName = "Nguyen Van A" });
        await (await c.Admin.PutAsJsonAsync($"/api/crm/students/{studentId}",
            new { fullName = "Nguyễn Văn A", birthYear = 2010, guardianName = "Phụ huynh A", phone = "+84 901 000 111", homeBranchId = c.BranchA }, Ct)).ShouldSucceed();
        var students = (await c.Admin.GetFromJsonAsync<StudentView[]>("/api/crm/students", Ct))!;
        students.Single().ShouldBe(new StudentView(studentId, "Nguyễn Văn A", 2010, c.BranchA, true));
        var details = (await c.Admin.GetFromJsonAsync<StudentDetail[]>("/api/crm/students/details", Ct))!;
        details.Single().ShouldBe(new StudentDetail(studentId, "Phụ huynh A", "0901000111"));
        var (_, teacher) = await c.AddStaffAsync("Teacher");
        (await teacher.GetAsync("/api/crm/students/details", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.GetAsync($"/api/crm/history/{studentId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var package = await c.PostIdAsync("/api/billing/packages", new { code = "P8", name = new { vi = "8 buổi" }, paidCredits = 8, bonusCredits = 0, price = 1600000, currency = "VND" });
        await (await c.Admin.PostAsJsonAsync($"/api/crm/students/{studentId}/deactivate", new { }, Ct)).ShouldSucceed();
        await (await c.Admin.PostAsJsonAsync("/api/billing/orders", new { studentId, packageId = package }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "billing.student_inactive");
        (await c.HistoryAsync("crm", studentId)).ShouldContain(h => h.Changes.Any(f => f.Field == "fullName" && f.Old == "Nguyen Van A"));

        var visitor = _api.ClientFor(c.Slug);
        (await visitor.PostAsJsonAsync("/api/crm/public/leads", new
        {
            studentName = "Lead A", phone = "0912345678", languageCode = "en", consentVersion = "v1", consentGiven = true,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var lead = (await c.Admin.GetFromJsonAsync<LeadView[]>("/api/crm/leads", Ct))!.Single();
        await (await c.Admin.PutAsJsonAsync($"/api/crm/leads/{lead.Id}",
            new { studentName = "Lead A", phone = "0912345678", languageCode = "ja", goal = "JLPT N5", preferredBranchId = c.BranchB }, Ct)).ShouldSucceed();
        (await c.Admin.GetFromJsonAsync<LeadView[]>("/api/crm/leads", Ct))!.Single().ShouldSatisfyAllConditions(
            l => l.LanguageCode.ShouldBe("ja"), l => l.Goal.ShouldBe("JLPT N5"), l => l.PreferredBranchId.ShouldBe(c.BranchB));
        await (await c.Admin.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/status", new { status = "Lost" }, Ct)).ShouldSucceed();
        await (await c.Admin.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new { studentName = "Lead A", phone = "0912345678", languageCode = "en" }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "crm.lead_closed");
    }

    [Fact]
    public async Task Packages_orders_and_payments_are_corrected_before_credits_exist()
    {
        var c = await EditCenter.CreateAsync(_api, "edbill");
        var package = await c.PostIdAsync("/api/billing/packages", new { code = "P10", name = new { vi = "10 buổi" }, paidCredits = 10, bonusCredits = 0, price = 2000000, currency = "VND" });
        await (await c.Admin.PutAsJsonAsync($"/api/billing/packages/{package}",
            new { code = "P10", name = new { vi = "10 buổi" }, paidCredits = 10, bonusCredits = 2, price = 2200000, currency = "VND" }, Ct)).ShouldSucceed();

        var studentId = await c.PostIdAsync("/api/crm/students", new { fullName = "HV" });
        var order = await c.PostIdAsync("/api/billing/orders", new { studentId, packageId = package });
        await (await c.Admin.PutAsJsonAsync($"/api/billing/orders/{order}", new { amount = 2000000 }, Ct)).ShouldSucceed();

        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = 500000, method = "cash" }, Ct)).ShouldSucceed();
        var payment = (await c.Admin.GetFromJsonAsync<PaymentView[]>($"/api/billing/orders/{order}/payments", Ct))!.Single();
        await (await c.Admin.PutAsJsonAsync($"/api/billing/payments/{payment.Id}", new { amount = 2000000, method = "cash" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.payment_amount_invalid");
        await (await c.Admin.PutAsJsonAsync($"/api/billing/payments/{payment.Id}", new { amount = 600000, method = "bank_transfer", reference = "FT123" }, Ct)).ShouldSucceed();
        await (await c.Admin.PutAsJsonAsync($"/api/billing/orders/{order}", new { amount = 600000 }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "billing.order_amount_invalid");
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/cancel", new { }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "billing.order_has_payments");

        await (await c.Admin.PostAsJsonAsync($"/api/billing/payments/{payment.Id}/void", new { }, Ct)).ShouldSucceed();
        await (await c.Admin.PostAsJsonAsync($"/api/billing/orders/{order}/cancel", new { }, Ct)).ShouldSucceed();
        var orders = (await c.Admin.GetFromJsonAsync<OrderView[]>("/api/billing/orders", Ct))!;
        orders.Single().ShouldBe(new OrderView(order, 2000000m, 0m, "Cancelled"));

        await (await c.Admin.PostAsJsonAsync($"/api/billing/packages/{package}/deactivate", new { }, Ct)).ShouldSucceed();
        (await c.Admin.GetFromJsonAsync<IdView[]>("/api/billing/packages", Ct))!.ShouldBeEmpty();
        (await c.Admin.GetFromJsonAsync<IdView[]>("/api/billing/packages?includeInactive=true", Ct))!.Length.ShouldBe(1);

        var history = await c.HistoryAsync("billing", order);
        history.ShouldContain(h => h.EntityType == "Payment" && h.Changes.Any(f => f.Field == "amount" && f.Old == "500000" && f.New == "600000"));
        history.ShouldContain(h => h.EntityType == "Order" && h.Changes.Any(f => f.Field == "status" && f.New == "Cancelled"));
    }

    private sealed record StaffView(Guid UserId, string DisplayName, string Status, GrantView[] Grants);

    private sealed record GrantView(string Role, Guid? BranchId);

    private sealed record AccessView(string Permission, bool TenantWide, Guid[] BranchIds);

    private sealed record RateView(Guid Id, Guid TeacherId, decimal HourlyRate, string Currency, string EffectiveFrom);

    private sealed record StudentView(Guid Id, string FullName, int? BirthYear, Guid? HomeBranchId, bool IsActive);

    private sealed record StudentDetail(Guid Id, string? GuardianName, string? Phone);

    private sealed record LeadView(Guid Id, string LanguageCode, string? Goal, Guid? PreferredBranchId);

    private sealed record PaymentView(Guid Id, decimal Amount);

    private sealed record OrderView(Guid Id, decimal Amount, decimal AmountReceived, string Status);
}
