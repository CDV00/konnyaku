using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Konnyaku.Assessment.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Konnyaku.IntegrationTests;

/// <summary>Assessment engine: banks, papers, assignments, attempts, grading, release policy and who sees what.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AssessmentTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Every_question_type_is_graded_on_submit_and_teacher_finishes_the_rest()
    {
        var c = await Center.CreateAsync(_api, db, "asgrade");
        var bank = await c.BankAsync("Grammar A1");
        var q = new Dictionary<string, Guid>
        {
            ["single"] = await c.QuestionAsync(bank, new
            {
                type = "SingleChoice", stem = "She ___ a student.", points = 1,
                content = new { options = Opts("is", "are", "am") }, key = new { choices = new[] { "a" } },
            }),
            ["multi"] = await c.QuestionAsync(bank, new
            {
                type = "MultipleChoice", stem = "Pick the fruits", points = 2,
                content = new { options = Opts("apple", "car", "pear") }, key = new { choices = new[] { "a", "c" } },
            }),
            ["truefalse"] = await c.QuestionAsync(bank, new { type = "TrueFalse", stem = "The sun is cold.", points = 1, key = new { choices = new[] { "false" } } }),
            ["blank"] = await c.QuestionAsync(bank, new
            {
                type = "FillBlank", stem = "I live in [[1]] and work in [[2]].", points = 2,
                key = new { blanks = new[] { new[] { "Hà Nội", "Hanoi" }, new[] { "Đà Nẵng" } }, ignoreCase = true, ignoreDiacritics = true },
            }),
            ["match"] = await c.QuestionAsync(bank, new
            {
                type = "Matching", stem = "Match the opposites", points = 3,
                content = new { options = Opts("hot", "big", "fast"), targets = new[] { new { id = "x", html = "cold" }, new { id = "y", html = "small" }, new { id = "z", html = "slow" } } },
                key = new { pairs = new Dictionary<string, string> { ["a"] = "x", ["b"] = "y", ["c"] = "z" } },
            }),
            ["order"] = await c.QuestionAsync(bank, new
            {
                type = "Ordering", stem = "Order the words", points = 2,
                content = new { options = Opts("I", "tea", "like") }, key = new { order = new[] { "a", "c", "b" } },
            }),
            ["short"] = await c.QuestionAsync(bank, new { type = "ShortAnswer", stem = "Past of go?", points = 2, key = new { accepted = new[] { "went" } } }),
            ["essay"] = await c.QuestionAsync(bank, new
            {
                type = "Essay", stem = "Write about your <b>summer</b><script>alert(1)</script>", points = 10,
                content = new { essay = new { min = 5, max = 200, count = "Words" } },
            }),
        };

        // The stem is sanitized: formatting kept, scripts gone.
        var detail = await c.Admin.GetFromJsonAsync<JsonElement>($"/api/assessment/banks/{bank}", Ct);
        var essayStem = detail.GetProperty("questions").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == q["essay"]).GetProperty("stem").GetString();
        essayStem!.ShouldContain("<b>summer</b>");
        essayStem!.ShouldNotContain("script");

        var paper = await c.PaperAsync("Unit test", new
        {
            sections = new[]
            {
                new { id = "s1", title = "Grammar", items = q.Values.Select(id => new { kind = "Question", questionId = id }).ToArray() },
            },
            rubrics = new[] { new { id = "w", name = "Writing", criteria = new[] { new { id = "content", name = "Content", maxBand = 5 }, new { id = "language", name = "Language", maxBand = 5 } } } },
        }, rubricFor: q["essay"]);
        var assignment = await c.AssignAsync(c.Teacher, paper, new { kind = "Quiz", scoreRelease = "OnSubmit", answerVisibility = "AfterSubmit" });

        var attempt = await c.StartAsync(c.Learners[0], assignment);
        var items = attempt.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(8);
        Guid Item(string name) => items.Single(i => i.GetProperty("stem").GetString()!.StartsWith(name switch
        {
            "single" => "She", "multi" => "Pick", "truefalse" => "The sun", "blank" => "I live", "match" => "Match", "order" => "Order", "short" => "Past",
            _ => "Write",
        }, StringComparison.Ordinal)).GetProperty("id").GetGuid();
        // The learner never receives the key.
        attempt.GetRawText().ShouldNotContain("\"key\"");

        var answers = new object[]
        {
            new { itemId = Item("single"), response = new { choices = new[] { "a" } } },
            new { itemId = Item("multi"), response = new { choices = new[] { "c", "a" } } },
            new { itemId = Item("truefalse"), response = new { choices = new[] { "true" } } },
            new { itemId = Item("blank"), response = new { blanks = new[] { "ha noi", "Hue" } } },
            new { itemId = Item("match"), response = new { pairs = new Dictionary<string, string> { ["a"] = "x", ["b"] = "z", ["c"] = "z" } } },
            new { itemId = Item("order"), response = new { order = new[] { "a", "c", "b" } } },
            new { itemId = Item("short"), response = new { text = "goed" } },
            new { itemId = Item("essay"), response = new { text = "My summer was long and sunny." }, flagged = true },
        };
        var learner = c.Learn(0);
        (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers", new { answers }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await learner.PostAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/events", new { kind = "TabHidden", value = 12 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var submitted = await (await learner.PostAsync($"/api/assessment/my/attempts/{Id(attempt)}/submit", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        submitted.GetProperty("status").GetString().ShouldBe("Submitted");
        submitted.GetProperty("pendingItems").GetInt32().ShouldBe(2);
        decimal? ScoreOf(JsonElement view, Guid itemId) =>
            view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId).GetProperty("score") is { ValueKind: JsonValueKind.Number } s
                ? s.GetDecimal() : null;
        ScoreOf(submitted, Item("single")).ShouldBe(1);
        ScoreOf(submitted, Item("multi")).ShouldBe(2);
        ScoreOf(submitted, Item("truefalse")).ShouldBe(0);
        ScoreOf(submitted, Item("blank")).ShouldBe(1);
        ScoreOf(submitted, Item("match")).ShouldBe(2);
        ScoreOf(submitted, Item("order")).ShouldBe(2);
        ScoreOf(submitted, Item("short")).ShouldBeNull();
        ScoreOf(submitted, Item("essay")).ShouldBeNull();
        submitted.GetProperty("rawScore").GetDecimal().ShouldBe(8);
        submitted.GetProperty("score").ValueKind.ShouldBe(JsonValueKind.Null);

        // The teacher's queue holds the attempt; the short answer and the essay (rubric) are graded, then the attempt finalized.
        var teacher = c.Teacher;
        var queue = await teacher.GetFromJsonAsync<JsonElement>("/api/assessment/grading", Ct);
        queue.EnumerateArray().Single().GetProperty("pendingItems").GetInt32().ShouldBe(2);
        await (await teacher.PostAsJsonAsync($"/api/assessment/attempts/{Id(attempt)}/finalize", new { }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.grading_incomplete");
        await (await teacher.PutAsJsonAsync($"/api/assessment/attempts/{Id(attempt)}/items/{Item("short")}/grade", new { score = 5 }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "assessment.score_invalid");
        await (await teacher.PutAsJsonAsync($"/api/assessment/attempts/{Id(attempt)}/items/{Item("short")}/grade", new { score = 1, comment = "went" }, Ct)).ShouldSucceed();
        await (await teacher.PutAsJsonAsync($"/api/assessment/attempts/{Id(attempt)}/items/{Item("essay")}/grade",
            new { bands = new Dictionary<string, decimal> { ["content"] = 4, ["language"] = 3 }, comment = "Good ideas" }, Ct)).ShouldSucceed();
        var review = await (await teacher.PostAsJsonAsync($"/api/assessment/attempts/{Id(attempt)}/finalize", new { comment = "Well done" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        review.GetProperty("status").GetString().ShouldBe("Graded");
        review.GetProperty("score").GetDecimal().ShouldBe(16); // 8 + 1 + 10 × 7/10
        review.GetProperty("events").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("TabHidden");
        (await teacher.GetFromJsonAsync<JsonElement>("/api/assessment/grading", Ct)).GetArrayLength().ShouldBe(0);

        var result = await learner.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(attempt)}/result", Ct);
        result.GetProperty("score").GetDecimal().ShouldBe(16);
        result.GetProperty("answersVisible").GetBoolean().ShouldBeTrue();
        result.GetProperty("items").EnumerateArray().First().GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Object);
        (await c.OutboxTypesAsync(Id(attempt))).ShouldContain(t => t.Contains("AssessmentGradedV1"));

        // Questions are versioned: editing one later does not change the attempt.
        (await c.Admin.PutAsJsonAsync($"/api/assessment/questions/{q["single"]}", new
        {
            type = "SingleChoice", stem = "He ___ a teacher.", points = 1, content = new { options = Opts("is", "are", "am") }, key = new { choices = new[] { "a" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await learner.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(attempt)}/result", Ct);
        again.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == Item("single")).GetProperty("stem").GetString()
            .ShouldBe("She ___ a student.");
    }

    [Fact]
    public async Task Expired_attempts_are_handed_in_by_the_worker_at_their_deadline()
    {
        var c = await Center.CreateAsync(_api, db, "asdead");
        var bank = await c.BankAsync("Vocabulary");
        var question = await c.QuestionAsync(bank, new { type = "TrueFalse", stem = "Cat is an animal.", points = 1, key = new { choices = new[] { "true" } } });
        var paper = await c.PaperAsync("Timed", new { sections = new[] { new { id = "s1", title = "All", items = new[] { new { kind = "Question", questionId = question } } } } });
        var assignment = await c.AssignAsync(c.Teacher, paper, new { kind = "Test", timeLimitMinutes = 30, maxAttempts = 1 });

        var attempt = await c.StartAsync(c.Learners[0], assignment);
        var deadline = attempt.GetProperty("deadlineAt").GetDateTimeOffset();
        (deadline - attempt.GetProperty("startedAt").GetDateTimeOffset()).ShouldBe(TimeSpan.FromMinutes(30));
        var learner = c.Learn(0);
        var item = attempt.GetProperty("items")[0].GetProperty("id").GetGuid();
        await (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers",
            new { answers = new[] { new { itemId = item, response = new { choices = new[] { "true" } } } } }, Ct)).ShouldSucceed();

        // Starting again resumes the same open attempt.
        Id(await c.StartAsync(c.Learners[0], assignment)).ShouldBe(Id(attempt));

        // The learner closed the browser; time runs out.
        await c.SqlAsync("UPDATE assessment.attempt SET started_at = started_at - interval '31 minutes', deadline_at = deadline_at - interval '31 minutes' WHERE id = @id",
            Id(attempt));
        var job = new AttemptDeadlineService(_api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AttemptDeadlineService>.Instance);
        (await job.RunOnceAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await job.RunOnceAsync(Ct)).ShouldBe(0);

        var result = await learner.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(attempt)}/result", Ct);
        result.GetProperty("status").GetString().ShouldBe("Graded");
        result.GetProperty("autoSubmitted").GetBoolean().ShouldBeTrue();
        result.GetProperty("submittedAt").GetDateTimeOffset().ShouldBe(deadline.AddMinutes(-31), TimeSpan.FromSeconds(1));
        result.GetProperty("score").GetDecimal().ShouldBe(1);
        await (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers",
            new { answers = new[] { new { itemId = item, response = new { choices = new[] { "false" } } } } }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.attempt_closed");

        // One attempt allowed, and a locked assignment past its due time cannot be started.
        await (await learner.PostAsync($"/api/assessment/my/assignments/{assignment}/attempts", null, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.attempts_exhausted");
        var locked = await c.AssignAsync(c.Teacher, paper, new
        {
            kind = "Homework", opensAt = DateTimeOffset.UtcNow.AddHours(-3), dueAt = DateTimeOffset.UtcNow.AddHours(-1), latePolicy = "Lock",
        });
        await (await learner.PostAsync($"/api/assessment/my/assignments/{locked}/attempts", null, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.assignment_closed");
    }

    [Fact]
    public async Task Scores_and_answers_follow_the_release_policy()
    {
        var c = await Center.CreateAsync(_api, db, "asrel");
        var bank = await c.BankAsync("Reading");
        var question = await c.QuestionAsync(bank, new { type = "TrueFalse", stem = "Water is wet.", points = 1, key = new { choices = new[] { "true" } } });
        var paper = await c.PaperAsync("Release", new { sections = new[] { new { id = "s1", title = "All", items = new[] { new { kind = "Question", questionId = question } } } } });

        // Released for the whole class at a set time; answers only after the due time.
        var assignment = await c.AssignAsync(c.Teacher, paper, new
        {
            kind = "Test", dueAt = DateTimeOffset.UtcNow.AddDays(2), scoreRelease = "AtTime", releaseAt = DateTimeOffset.UtcNow.AddDays(3),
            answerVisibility = "AfterDue", maxAttempts = 2, scoringRule = "Best",
        });
        var attempt = await c.StartAsync(c.Learners[0], assignment);
        var learner = c.Learn(0);
        var item = attempt.GetProperty("items")[0].GetProperty("id").GetGuid();
        await (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers",
            new { answers = new[] { new { itemId = item, response = new { choices = new[] { "true" } } } } }, Ct)).ShouldSucceed();
        var hidden = await (await learner.PostAsync($"/api/assessment/my/attempts/{Id(attempt)}/submit", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        hidden.GetProperty("status").GetString().ShouldBe("Graded");
        hidden.GetProperty("scoresVisible").GetBoolean().ShouldBeFalse();
        hidden.GetProperty("score").ValueKind.ShouldBe(JsonValueKind.Null);
        var hiddenItem = hidden.GetProperty("items")[0];
        hiddenItem.GetProperty("score").ValueKind.ShouldBe(JsonValueKind.Null);
        hiddenItem.GetProperty("correct").ValueKind.ShouldBe(JsonValueKind.Null);
        hiddenItem.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);
        var mine = (await learner.GetFromJsonAsync<JsonElement>("/api/assessment/my/assignments", Ct)).EnumerateArray().Single();
        mine.GetProperty("score").ValueKind.ShouldBe(JsonValueKind.Null);
        mine.GetProperty("canStart").GetBoolean().ShouldBeTrue();

        // The teacher releases now: scores show, answers still wait for the due time.
        await (await c.Teacher.PostAsync($"/api/assessment/assignments/{assignment}/release", null, Ct)).ShouldSucceed();
        var released = await learner.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(attempt)}/result", Ct);
        released.GetProperty("score").GetDecimal().ShouldBe(1);
        released.GetProperty("items")[0].GetProperty("correct").GetBoolean().ShouldBeTrue();
        released.GetProperty("answersVisible").GetBoolean().ShouldBeFalse();
        released.GetProperty("items")[0].GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);

        // After grading: an essay keeps the score hidden until the teacher finishes.
        var essay = await c.QuestionAsync(bank, new { type = "Essay", stem = "Describe your room.", points = 5 });
        var essayPaper = await c.PaperAsync("Essay", new { sections = new[] { new { id = "s1", title = "Writing", items = new[] { new { kind = "Question", questionId = essay } } } } });
        var essayAssignment = await c.AssignAsync(c.Teacher, essayPaper, new { kind = "Homework", scoreRelease = "AfterGrading" });
        var essayAttempt = await c.StartAsync(c.Learners[1], essayAssignment);
        var learner2 = c.Learn(1);
        var essayItem = essayAttempt.GetProperty("items")[0].GetProperty("id").GetGuid();
        await (await learner2.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(essayAttempt)}/answers",
            new { answers = new[] { new { itemId = essayItem, response = new { text = "My room is small and bright." } } } }, Ct)).ShouldSucceed();
        var pending = await (await learner2.PostAsync($"/api/assessment/my/attempts/{Id(essayAttempt)}/submit", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        pending.GetProperty("scoresVisible").GetBoolean().ShouldBeFalse();
        await (await c.Teacher.PutAsJsonAsync($"/api/assessment/attempts/{Id(essayAttempt)}/items/{essayItem}/grade", new { score = 4 }, Ct)).ShouldSucceed();
        (await learner2.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(essayAttempt)}/result", Ct)).GetProperty("scoresVisible").GetBoolean().ShouldBeFalse();
        await (await c.Teacher.PostAsJsonAsync($"/api/assessment/attempts/{Id(essayAttempt)}/finalize", new { }, Ct)).ShouldSucceed();
        var graded = await learner2.GetFromJsonAsync<JsonElement>($"/api/assessment/my/attempts/{Id(essayAttempt)}/result", Ct);
        graded.GetProperty("score").GetDecimal().ShouldBe(4);
        graded.GetProperty("answersVisible").GetBoolean().ShouldBeFalse(); // default: never
    }

    [Fact]
    public async Task Learners_see_only_their_own_work_and_staff_only_their_classes()
    {
        var c = await Center.CreateAsync(_api, db, "asiso");
        var bank = await c.BankAsync("Listening");
        var question = await c.QuestionAsync(bank, new { type = "TrueFalse", stem = "Hi", points = 1, key = new { choices = new[] { "true" } } });
        var paper = await c.PaperAsync("Iso", new { sections = new[] { new { id = "s1", title = "All", items = new[] { new { kind = "Question", questionId = question } } } } });

        // Only learner 0 is targeted.
        var assignment = await c.AssignAsync(c.Teacher, paper, new { kind = "Quiz", target = "Students", studentIds = new[] { c.Learners[0].StudentId } });
        var attempt = await c.StartAsync(c.Learners[0], assignment);

        var other = c.Learn(1);
        (await other.GetFromJsonAsync<JsonElement>("/api/assessment/my/assignments", Ct)).GetArrayLength().ShouldBe(0);
        await (await other.PostAsync($"/api/assessment/my/assignments/{assignment}/attempts", null, Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.assignment_not_found");
        await (await other.GetAsync($"/api/assessment/my/attempts/{Id(attempt)}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.attempt_not_found");
        await (await other.PostAsync($"/api/assessment/my/attempts/{Id(attempt)}/submit", null, Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.attempt_not_found");
        // A learner token never reaches staff routes.
        (await other.GetAsync("/api/assessment/grading", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.GetAsync($"/api/assessment/attempts/{Id(attempt)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A learner of another class sees nothing.
        var stranger = await c.LearnerInNewClassAsync();
        (await stranger.GetFromJsonAsync<JsonElement>("/api/assessment/my/assignments", Ct)).GetArrayLength().ShouldBe(0);

        // Staff: the other teacher, a coordinator of the other branch and a consultant cannot reach the class's work.
        var otherTeacher = await c.Edit.SignInAsync(c.Edit.OtherTeacherId);
        var (_, coordinatorB) = await c.Edit.AddStaffAsync("Coordinator", c.Edit.BranchB);
        var (_, managerA) = await c.Edit.AddStaffAsync("BranchManager", c.Edit.BranchA);
        var (_, consultant) = await c.Edit.AddStaffAsync("Consultant");
        foreach (var outsider in new[] { otherTeacher, coordinatorB })
        {
            await (await outsider.GetAsync($"/api/assessment/assignments/{assignment}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.assignment_not_found");
            await (await outsider.GetAsync($"/api/assessment/attempts/{Id(attempt)}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.attempt_not_found");
            (await outsider.GetFromJsonAsync<JsonElement>("/api/assessment/grading", Ct)).GetArrayLength().ShouldBe(0);
        }

        await (await otherTeacher.PostAsJsonAsync("/api/assessment/assignments", new { paperId = paper, title = "x", target = "Class", classId = c.ClassId, kind = "Quiz" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await consultant.PostAsJsonAsync("/api/assessment/banks", new { name = "Nope", languageCode = "en", visibility = "Shared" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        (await consultant.GetAsync("/api/assessment/overview", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await otherTeacher.GetAsync("/api/assessment/overview", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The branch manager of the class's branch sees and grades it; the private bank of the teacher stays private to other teachers.
        (await managerA.GetAsync($"/api/assessment/attempts/{Id(attempt)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await managerA.GetFromJsonAsync<JsonElement>("/api/assessment/overview", Ct)).GetProperty("assignments").GetInt32().ShouldBe(1);
        (await coordinatorB.GetFromJsonAsync<JsonElement>("/api/assessment/overview", Ct)).GetProperty("assignments").GetInt32().ShouldBe(0);
        var privateBank = await c.BankAsync("Mine", c.Teacher, "Private");
        await (await otherTeacher.GetAsync($"/api/assessment/banks/{privateBank}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.bank_not_found");
        (await otherTeacher.GetAsync($"/api/assessment/banks/{bank}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await otherTeacher.PostAsJsonAsync($"/api/assessment/banks/{bank}/questions",
            new { type = "TrueFalse", stem = "x", points = 1, key = new { choices = new[] { "true" } } }, Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");

        // Another center sees none of it (tenant isolation through RLS and the query filter).
        var otherCenter = await Center.CreateAsync(_api, db, "asiso2");
        await (await otherCenter.Admin.GetAsync($"/api/assessment/assignments/{assignment}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.assignment_not_found");
        await (await otherCenter.Admin.GetAsync($"/api/assessment/banks/{bank}", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "assessment.bank_not_found");
    }

    [Fact]
    public async Task Random_draws_timed_sections_and_reading_groups()
    {
        var c = await Center.CreateAsync(_api, db, "asrand");
        var bank = await c.BankAsync("Mixed");
        for (var i = 0; i < 3; i++)
        {
            await c.QuestionAsync(bank, new { type = "TrueFalse", stem = $"Grammar {i}", points = 1, key = new { choices = new[] { "true" } }, tags = new[] { "Grammar", "unit1" } });
        }

        await c.QuestionAsync(bank, new { type = "TrueFalse", stem = "Other", points = 1, key = new { choices = new[] { "true" } }, tags = new[] { "vocab" } });
        var group = Id(await c.PostAsync($"/api/assessment/banks/{bank}/groups", new { title = "Passage", passage = "Tom has a <ruby>猫<rt>ねこ</rt></ruby>." }));
        foreach (var stem in new[] { "Tom has a cat.", "Tom has a dog." })
        {
            await c.QuestionAsync(bank, new { type = "TrueFalse", stem, points = 1, key = new { choices = new[] { "true" } }, groupId = group });
        }

        await (await c.Admin.PostAsJsonAsync("/api/assessment/papers", new
        {
            title = "Too many", languageCode = "en", showRuby = true,
            layout = new { sections = new[] { new { id = "s1", title = "G", items = new[] { new { kind = "Random", bankId = bank, tags = new[] { "grammar" }, count = 4 } } } } },
        }, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "assessment.random_pool_too_small");
        await (await c.Admin.PostAsJsonAsync("/api/assessment/papers", new
        {
            title = "Mixed limits", languageCode = "en", showRuby = true,
            layout = new
            {
                sections = new object[]
                {
                    new { id = "s1", title = "A", timeLimitMinutes = 10, items = new[] { new { kind = "Group", groupId = group } } },
                    new { id = "s2", title = "B", items = new[] { new { kind = "Group", groupId = group } } },
                },
            },
        }, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "assessment.section_time_limits_mixed");

        var paper = await c.PaperAsync("JLPT-like", new
        {
            sections = new object[]
            {
                new { id = "s1", title = "Grammar", timeLimitMinutes = 10, items = new[] { new { kind = "Random", bankId = (Guid?)bank, groupId = (Guid?)null, tags = new[] { "grammar" }, count = 2 } } },
                new { id = "s2", title = "Reading", timeLimitMinutes = 20, items = new[] { new { kind = "Group", bankId = (Guid?)null, groupId = (Guid?)group, tags = Array.Empty<string>(), count = 1 } } },
            },
        });
        var assignment = await c.AssignAsync(c.Teacher, paper, new { kind = "MockExam" });
        var attempt = await c.StartAsync(c.Learners[0], assignment);
        attempt.GetProperty("timed").GetBoolean().ShouldBeTrue();
        (attempt.GetProperty("deadlineAt").GetDateTimeOffset() - attempt.GetProperty("startedAt").GetDateTimeOffset()).ShouldBe(TimeSpan.FromMinutes(30));
        var items = attempt.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(4);
        items.Take(2).ShouldAllBe(i => i.GetProperty("stem").GetString()!.StartsWith("Grammar", StringComparison.Ordinal));

        // The reading section is locked (no content) until the grammar section is over.
        var reading = items.Skip(2).ToList();
        reading.ShouldAllBe(i => i.GetProperty("locked").GetBoolean() && i.GetProperty("stem").GetString() == string.Empty);
        var learner = c.Learn(0);
        await (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers",
            new { answers = new[] { new { itemId = reading[0].GetProperty("id").GetGuid(), response = new { choices = new[] { "true" } } } } }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.section_closed");

        var next = await (await learner.PostAsync($"/api/assessment/my/attempts/{Id(attempt)}/next-section", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        next.GetProperty("sectionIndex").GetInt32().ShouldBe(1);
        var unlocked = next.GetProperty("items").EnumerateArray().Skip(2).ToList();
        unlocked.ShouldAllBe(i => !i.GetProperty("locked").GetBoolean());
        unlocked[0].GetProperty("passage").GetString()!.ShouldContain("<ruby>");
        await (await learner.PutAsJsonAsync($"/api/assessment/my/attempts/{Id(attempt)}/answers",
            new { answers = new[] { new { itemId = items[0].GetProperty("id").GetGuid(), response = new { choices = new[] { "true" } } } } }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.section_closed");
        await (await learner.PostAsync($"/api/assessment/my/attempts/{Id(attempt)}/next-section", null, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "assessment.last_section");
    }

    private static object[] Opts(params string[] texts) => texts.Select((t, i) => new { id = ((char)('a' + i)).ToString(), html = t }).ToArray<object>();

    private static Guid Id(JsonElement json) => json.GetProperty("id").GetGuid();

    private sealed record Learner(Guid UserId, Guid StudentId, string Token);

    /// <summary>A center (two branches) with one class in branch A taught by the teacher, and two learners with accounts in it.</summary>
    private sealed class Center
    {
        private readonly KonnyakuApiFactory _api;
        private readonly PostgresFixture _db;

        private Center(KonnyakuApiFactory api, PostgresFixture db, EditCenter edit)
        {
            _api = api;
            _db = db;
            Edit = edit;
        }

        public EditCenter Edit { get; }
        public HttpClient Admin => Edit.Admin;
        public HttpClient Teacher { get; private set; } = null!;
        public Guid ClassId { get; private set; }
        public List<Learner> Learners { get; } = [];

        public static async Task<Center> CreateAsync(KonnyakuApiFactory api, PostgresFixture db, string prefix)
        {
            var edit = await EditCenter.CreateAsync(api, prefix);
            var center = new Center(api, db, edit);
            center.ClassId = await edit.CreateClassAsync("EN-A1-01", edit.BranchA);
            await edit.ScheduleAsync(center.ClassId, [1, 3], "18:00", edit.TeacherId);
            center.Teacher = await edit.SignInAsync(edit.TeacherId);
            center.Learners.Add(await center.EnrollLearnerAsync(center.ClassId, "An"));
            center.Learners.Add(await center.EnrollLearnerAsync(center.ClassId, "Bình"));
            return center;
        }

        public HttpClient Learn(int index) => _api.ClientFor(Edit.Slug, Learners[index].Token);

        public async Task<HttpClient> LearnerInNewClassAsync()
        {
            var cls = await Edit.CreateClassAsync($"EN-A1-{Guid.NewGuid():N}"[..12], Edit.BranchA);
            return _api.ClientFor(Edit.Slug, (await EnrollLearnerAsync(cls, "Chi")).Token);
        }

        public async Task<JsonElement> PostAsync(string path, object body, HttpClient? client = null)
        {
            var response = await (client ?? Admin).PostAsJsonAsync(path, body, Ct);
            await response.ShouldSucceed();
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        public async Task<Guid> BankAsync(string name, HttpClient? owner = null, string visibility = "Shared") =>
            Id(await PostAsync("/api/assessment/banks", new { name, languageCode = "en", visibility }, owner ?? Teacher));

        public async Task<Guid> QuestionAsync(Guid bank, object body) => Id(await PostAsync($"/api/assessment/banks/{bank}/questions", body, Teacher));

        public async Task<Guid> PaperAsync(string title, object layout, Guid? rubricFor = null)
        {
            if (rubricFor is { } essay)
            {
                // Attach the rubric to the essay item.
                var json = JsonSerializer.SerializeToNode(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                foreach (var item in json["sections"]![0]!["items"]!.AsArray())
                {
                    if (item!["questionId"]!.GetValue<Guid>() == essay)
                    {
                        item["rubricId"] = "w";
                    }
                }

                layout = json;
            }

            return Id(await PostAsync("/api/assessment/papers", new { title, languageCode = "en", showRuby = true, layout }, Teacher));
        }

        /// <summary>Assigns the paper to the class (settings merged over sensible defaults).</summary>
        public async Task<Guid> AssignAsync(HttpClient client, Guid paper, object settings)
        {
            var body = JsonSerializer.SerializeToNode(new
            {
                paperId = paper, title = "Assignment", target = "Class", classId = ClassId, kind = "Quiz", latePolicy = "Accept", latePenaltyPercent = 0,
                scoringRule = "Best", grader = "Auto", scoreRelease = "OnSubmit", answerVisibility = "Never",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            foreach (var (key, value) in JsonSerializer.SerializeToNode(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject())
            {
                body[key] = value?.DeepClone();
            }

            return Id(await PostAsync("/api/assessment/assignments", body, client));
        }

        public async Task<JsonElement> StartAsync(Learner learner, Guid assignment)
        {
            var response = await _api.ClientFor(Edit.Slug, learner.Token).PostAsync($"/api/assessment/my/assignments/{assignment}/attempts", null, Ct);
            await response.ShouldSucceed();
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        /// <summary>Events queued in the module outbox that mention <paramref name="id"/> (owner connection: no RLS).</summary>
        public async Task<List<string>> OutboxTypesAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_db.OwnerConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT type FROM assessment.outbox_message WHERE payload::text LIKE '%' || @id || '%'", connection);
            command.Parameters.AddWithValue("id", id.ToString());
            var types = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                types.Add(reader.GetString(0));
            }

            return types;
        }

        public async Task SqlAsync(string sql, Guid id)
        {
            await using var connection = new NpgsqlConnection(_db.OwnerConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        private async Task<Learner> EnrollLearnerAsync(Guid classId, string name)
        {
            var account = await PostAsync("/api/identity/learners", new { displayName = name, role = "Student" });
            var userId = account.GetProperty("userId").GetGuid();
            var student = await Edit.PostIdAsync("/api/crm/students", new { fullName = name, userId });
            (await Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = student, type = "Regular" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            var login = await _api.ClientFor(Edit.Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account.GetProperty("username").GetString(), password = account.GetProperty("temporaryPassword").GetString() }, Ct);
            await login.ShouldSucceed();
            return new Learner(userId, student, (await login.Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken);
        }
    }
}
