using Konnyaku.Assessment.Domain;

namespace Konnyaku.UnitTests;

public class AutoGraderTests
{
    private static AnswerKey Key(Action<AnswerKey> set)
    {
        var key = new AnswerKey();
        set(key);
        return key;
    }

    [Fact]
    public void Single_choice_and_true_false_are_all_or_nothing()
    {
        var key = Key(k => k.Choices = ["b"]);
        AutoGrader.Grade(QuestionType.SingleChoice, key, new ItemResponse { Choices = ["b"] }, 2).ShouldBe(AutoGrade.Scored(2));
        AutoGrader.Grade(QuestionType.SingleChoice, key, new ItemResponse { Choices = ["a"] }, 2).ShouldBe(AutoGrade.Scored(0));
        AutoGrader.Grade(QuestionType.SingleChoice, key, new ItemResponse { Choices = ["a", "b"] }, 2).ShouldBe(AutoGrade.Scored(0));
        AutoGrader.Grade(QuestionType.TrueFalse, Key(k => k.Choices = ["false"]), new ItemResponse { Choices = ["false"] }, 1).ShouldBe(AutoGrade.Scored(1));
        AutoGrader.Grade(QuestionType.TrueFalse, Key(k => k.Choices = ["false"]), null, 1).ShouldBe(AutoGrade.Scored(0));
    }

    [Fact]
    public void Multiple_choice_needs_exactly_the_correct_set()
    {
        var key = Key(k => k.Choices = ["a", "c"]);
        AutoGrader.Grade(QuestionType.MultipleChoice, key, new ItemResponse { Choices = ["c", "a"] }, 3).ShouldBe(AutoGrade.Scored(3));
        AutoGrader.Grade(QuestionType.MultipleChoice, key, new ItemResponse { Choices = ["a"] }, 3).ShouldBe(AutoGrade.Scored(0));
        AutoGrader.Grade(QuestionType.MultipleChoice, key, new ItemResponse { Choices = ["a", "b", "c"] }, 3).ShouldBe(AutoGrade.Scored(0));
    }

    [Fact]
    public void Ordering_is_all_or_nothing_and_matching_scores_per_pair()
    {
        AutoGrader.Grade(QuestionType.Ordering, Key(k => k.Order = ["c", "a", "b"]), new ItemResponse { Order = ["c", "a", "b"] }, 2)
            .ShouldBe(AutoGrade.Scored(2));
        AutoGrader.Grade(QuestionType.Ordering, Key(k => k.Order = ["c", "a", "b"]), new ItemResponse { Order = ["a", "c", "b"] }, 2)
            .ShouldBe(AutoGrade.Scored(0));

        var pairs = Key(k => k.Pairs = new() { ["1"] = "x", ["2"] = "y", ["3"] = "z" });
        AutoGrader.Grade(QuestionType.Matching, pairs, new ItemResponse { Pairs = new() { ["1"] = "x", ["2"] = "z", ["3"] = "z" } }, 3)
            .ShouldBe(AutoGrade.Scored(2));
        AutoGrader.Grade(QuestionType.Matching, pairs, new ItemResponse { Pairs = new() { ["1"] = "x", ["2"] = "y", ["3"] = "z" } }, 3)
            .ShouldBe(AutoGrade.Scored(3));
    }

    [Fact]
    public void Blanks_accept_listed_spellings_and_score_per_blank()
    {
        // 私は [[1]] を食べます。[[2]] へ行きます。 — kana and kanji spellings are both listed by the author.
        var key = Key(k => k.Blanks = [["りんご", "林檎"], ["学校", "がっこう"]]);
        AutoGrader.Grade(QuestionType.FillBlank, key, new ItemResponse { Blanks = ["林檎", "がっこう"] }, 2).ShouldBe(AutoGrade.Scored(2));
        AutoGrader.Grade(QuestionType.FillBlank, key, new ItemResponse { Blanks = [" りんご ", "がっこ"] }, 2).ShouldBe(AutoGrade.Scored(1));
        AutoGrader.Grade(QuestionType.FillBlank, key, new ItemResponse { Blanks = ["りんご"] }, 2).ShouldBe(AutoGrade.Scored(1));
    }

    [Fact]
    public void Kana_voicing_marks_survive_the_diacritics_option()
    {
        var options = Key(k => k.IgnoreDiacritics = true);
        AnswerMatcher.Matches("がっこう", ["かっこう"], options).ShouldBeFalse();
        AnswerMatcher.Matches("Hà Nội", ["ha noi"], options).ShouldBeTrue();
        AnswerMatcher.Matches("Đà Nẵng", ["da nang"], options).ShouldBeTrue();
        AnswerMatcher.Matches("Hà Nội", ["ha noi"], Key(k => k.IgnoreDiacritics = false)).ShouldBeFalse();
    }

    [Fact]
    public void Case_width_and_spaces_are_folded()
    {
        AnswerMatcher.Matches("  New   York ", ["new york"], new AnswerKey()).ShouldBeTrue();
        AnswerMatcher.Matches("ＡＢＣ", ["abc"], new AnswerKey()).ShouldBeTrue();
        AnswerMatcher.Matches("ｶﾀｶﾅ", ["カタカナ"], new AnswerKey()).ShouldBeTrue();
        AnswerMatcher.Matches("Paris", ["paris"], Key(k => k.IgnoreCase = false)).ShouldBeFalse();
    }

    [Fact]
    public void Short_answer_mismatch_and_essays_go_to_the_teacher()
    {
        var key = Key(k => k.Accepted = ["went", "had gone"]);
        AutoGrader.Grade(QuestionType.ShortAnswer, key, new ItemResponse { Text = "Went" }, 2).ShouldBe(AutoGrade.Scored(2));
        AutoGrader.Grade(QuestionType.ShortAnswer, key, new ItemResponse { Text = "goed" }, 2).ShouldBe(AutoGrade.Teacher);
        AutoGrader.Grade(QuestionType.ShortAnswer, key, new ItemResponse(), 2).ShouldBe(AutoGrade.Scored(0));
        AutoGrader.Grade(QuestionType.Essay, new AnswerKey(), new ItemResponse { Text = "My summer…" }, 10).ShouldBe(AutoGrade.Teacher);
        AutoGrader.Grade(QuestionType.Essay, new AnswerKey(), new ItemResponse { Text = " " }, 10).ShouldBe(AutoGrade.Scored(0));
    }

    [Fact]
    public void Essays_count_words_or_characters()
    {
        AnswerMatcher.Count("I like green tea.", CountMode.Words).ShouldBe(4);
        AnswerMatcher.Count("私は学生です。", CountMode.Characters).ShouldBe(6);
        AnswerMatcher.Count("我是学生，你呢？", CountMode.Characters).ShouldBe(6);
    }

    [Fact]
    public void Rubric_bands_scale_to_the_item_points()
    {
        var rubric = new Rubric
        {
            Id = "ielts",
            Name = "IELTS Writing",
            Criteria = [new() { Id = "tr", MaxBand = 9 }, new() { Id = "cc", MaxBand = 9 }, new() { Id = "lr", MaxBand = 9 }, new() { Id = "gra", MaxBand = 9 }],
        };
        rubric.Score(new Dictionary<string, decimal> { ["tr"] = 6, ["cc"] = 7, ["lr"] = 6, ["gra"] = 5 }, 20).ShouldBe(13.33m);
        rubric.Score(new Dictionary<string, decimal> { ["tr"] = 6 }, 20).ShouldBeNull();
        rubric.Score(new Dictionary<string, decimal> { ["tr"] = 10, ["cc"] = 7, ["lr"] = 6, ["gra"] = 5 }, 20).ShouldBeNull();
    }

    [Fact]
    public void Question_rules_reject_broken_keys()
    {
        QuestionData Data(QuestionType type, string stem, QuestionContent content, AnswerKey key) =>
            new(type, stem, content, key, 1, null, null, null, [], null, null);
        var options = new QuestionContent { Options = [new() { Id = "a", Html = "A" }, new() { Id = "b", Html = "B" }] };

        QuestionRules.Validate(Data(QuestionType.SingleChoice, "Q", options, Key(k => k.Choices = ["z"])))!.Code.ShouldBe("assessment.answer_key_invalid");
        QuestionRules.Validate(Data(QuestionType.SingleChoice, "Q", options, Key(k => k.Choices = ["a"]))).ShouldBeNull();
        QuestionRules.Validate(Data(QuestionType.FillBlank, "No blank here", new QuestionContent(), Key(k => k.Blanks = [["x"]])))!.Code
            .ShouldBe("assessment.blanks_missing");
        QuestionRules.Validate(Data(QuestionType.FillBlank, "A [[1]] B [[2]]", new QuestionContent(), Key(k => k.Blanks = [["x"]])))!.Code
            .ShouldBe("assessment.answer_key_invalid");
        QuestionRules.Validate(Data(QuestionType.Essay, "<p> </p>", new QuestionContent(), new AnswerKey()))!.Code.ShouldBe("assessment.stem_required");
        QuestionRules.BlankMarkers("A [[2]] B [[1]] C [[2]]").ShouldBe(2);
        QuestionRules.BlankMarkers("A [[2]]").ShouldBe(0);
    }

    [Fact]
    public void Release_policy_and_final_score()
    {
        ReleasePolicy.FinalScore(ScoringRule.Best, [4, 9, 6]).ShouldBe(9);
        ReleasePolicy.FinalScore(ScoringRule.Last, [4, 9, 6]).ShouldBe(6);
        ReleasePolicy.FinalScore(ScoringRule.Average, [4, 9, 6]).ShouldBe(6.33m);
        ReleasePolicy.FinalScore(ScoringRule.Best, []).ShouldBeNull();
    }
}
