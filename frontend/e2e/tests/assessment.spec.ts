import { expect, test } from "@playwright/test";
import { Api, createClass, createLearner, type ClassInfo, type Learner } from "../support/api";
import { seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { hydrated, signIn } from "../support/ui";

/**
 * A quiz of its own: the class's own teacher writes three questions (single choice, fill in the blank, essay) in a bank,
 * builds a paper and assigns it to the class with scores released on submit.
 */
async function quiz(admin: Api, cls: ClassInfo, learner: Learner, title: string) {
  const enrolled = await admin.call("POST", `/api/academic/classes/${cls.id}/enrollments`, { studentId: learner.studentId, type: "Regular" });
  expect(enrolled.status).toBe(201);
  const teacher = await Api.staff(cls.teacher.email, cls.teacher.password);
  const bank = await teacher.post<{ id: string }>("/api/assessment/banks", { name: `Bank ${title}`, languageCode: "en", visibility: "Private" });
  const question = (body: object) => teacher.post<{ id: string }>(`/api/assessment/banks/${bank.id}/questions`, body);
  const single = await question({
    type: "SingleChoice", stem: "She ___ to school every day.", points: 1,
    content: { options: [{ id: "a", html: "goes" }, { id: "b", html: "go" }, { id: "c", html: "going" }], shuffle: true }, key: { choices: ["a"] },
  });
  const blank = await question({ type: "FillBlank", stem: "I live in [[1]].", points: 1, key: { blanks: [["Hà Nội", "Hanoi"]], ignoreCase: true, ignoreDiacritics: true } });
  const essay = await question({ type: "Essay", stem: "Describe your room.", points: 5, content: { essay: { min: 3, max: 100, count: "Words" } } });
  const paper = await teacher.post<{ id: string }>("/api/assessment/papers", {
    title, languageCode: "en", showRuby: true,
    layout: { sections: [{ id: "s1", title: "All", items: [single, blank, essay].map((q) => ({ kind: "Question", questionId: q.id })) }] },
  });
  await teacher.post("/api/assessment/assignments", {
    paperId: paper.id, title, target: "Class", classId: cls.id, kind: "Quiz", latePolicy: "Accept", latePenaltyPercent: 0, maxAttempts: 1,
    scoringRule: "Best", grader: "Auto", scoreRelease: "OnSubmit", answerVisibility: "AfterSubmit", timeLimitMinutes: 30,
  });
}

test("learner takes a quiz, the teacher grades the essay, the learner sees the score", async ({ page, browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin);
  const learner = await createLearner(admin);
  const title = `E2E quiz ${uniqueTag()}`;
  await quiz(admin, cls, learner, title);

  const learnerContext = await browser.newContext();
  const learnerPage = await learnerContext.newPage();

  await test.step("the learner finds the quiz, answers one question per screen and submits", async () => {
    await signIn(learnerPage, "learn", learner.username, learner.password);
    await learnerPage.goto("/learn/assessments");
    await learnerPage.getByRole("link").filter({ hasText: title }).click();
    await (await hydrated(learnerPage.getByRole("button", { name: t("assessment.learn.start") }))).click();
    await learnerPage.waitForURL(/\/learn\/attempts\/[0-9a-f-]+$/);

    await (await hydrated(learnerPage.locator("label").filter({ hasText: "goes" }))).click();
    await learnerPage.getByRole("button", { name: t("assessment.player.next") }).click();
    await learnerPage.getByLabel(t("assessment.player.blank", { number: 1 })).fill("ha noi");
    await learnerPage.getByRole("button", { name: t("assessment.player.next") }).click();
    await learnerPage.getByLabel(t("assessment.player.yourAnswer")).fill("My room is small but bright and quiet.");
    await learnerPage.getByRole("button", { name: t("assessment.player.submit") }).click();
    await learnerPage.getByRole("button", { name: t("assessment.player.confirm") }).click();

    await learnerPage.waitForURL(/\/result$/);
    await expect(learnerPage.getByText(t("assessment.learn.waitingTeacher"), { exact: true })).toBeVisible();
  });

  await test.step("the teacher grades the essay from the grading queue and finishes", async () => {
    await signIn(page, "staff", cls.teacher.email, cls.teacher.password, /\/teach/);
    await page.goto("/teach/assessments/grading");
    await page.getByRole("link", { name: learner.name }).click();
    await (await hydrated(page.locator('input[name="score"]'))).fill("4");
    await page.getByRole("button", { name: t("assessment.grade.save") }).click();
    const finish = page.getByRole("button", { name: t("assessment.grade.finish") });
    await expect(finish).toBeEnabled();
    await finish.click();
    await expect(page.getByText(t("assessment.grade.gradedTitle"), { exact: true })).toBeVisible();
  });

  await test.step("the learner sees the official score: 1 + 1 + 4 of 7", async () => {
    await learnerPage.reload();
    await expect(learnerPage.getByText(t("assessment.learn.scoreValue", { score: 6, max: 7 })).first()).toBeVisible();
    await expect(learnerPage.getByText(t("assessment.learn.waitingTeacher"), { exact: true })).toHaveCount(0);
  });
  await learnerContext.close();

  await test.step("another learner of the center does not see the quiz", async () => {
    const otherContext = await browser.newContext();
    const other = await otherContext.newPage();
    await signIn(other, "learn", seed.learner(12), seed.learnerPassword);
    await other.goto("/learn/assessments");
    await expect(other.getByRole("heading", { name: t("assessment.learn.title") })).toBeVisible();
    await expect(other.getByText(title)).toHaveCount(0);
    await otherContext.close();
  });
});
