import { expect, test, type Locator, type Page } from "@playwright/test";
import { Api, createClass, createLearner, type ClassInfo, type Learner } from "../support/api";
import { seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { hydrated, openSheet, signIn } from "../support/ui";

/** The <select> of a Field: its label wraps the select, so the accessible name also holds every option's text. */
const fieldSelect = (scope: Page | Locator, label: string) => {
  const root = "page" in scope ? scope.page() : scope;
  const exact = new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`);
  return scope.locator("label").filter({ has: root.locator("span").filter({ hasText: exact }) }).locator("select");
};

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

  await test.step("a learner of another class does not see the quiz", async () => {
    const otherClass = await createClass(admin);
    const outsider = await createLearner(admin);
    const enrolled = await admin.call("POST", `/api/academic/classes/${otherClass.id}/enrollments`, { studentId: outsider.studentId, type: "Regular" });
    expect(enrolled.status).toBe(201);
    const otherContext = await browser.newContext();
    const other = await otherContext.newPage();
    await signIn(other, "learn", outsider.username, outsider.password);
    await other.goto("/learn/assessments");
    await expect(other.getByRole("heading", { name: t("assessment.learn.title") })).toBeVisible();
    await expect(other.getByText(title)).toHaveCount(0);
    await otherContext.close();
  });
});

test("teacher builds a quiz in the UI and assigns it; the learner's answers survive a reload and are graded on submit", async ({ page, browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin);
  const learner = await createLearner(admin);
  const enrolled = await admin.call("POST", `/api/academic/classes/${cls.id}/enrollments`, { studentId: learner.studentId, type: "Regular" });
  expect(enrolled.status).toBe(201);
  const tag = uniqueTag();
  const bankName = `E2E bank ${tag}`;
  const paperTitle = `E2E paper ${tag}`;
  const questions = [
    { stem: `Q1 ${tag}: the cat [猫|ねこ] is an animal`, right: "yes", wrong: "no" },
    { stem: `Q2 ${tag}: two plus two`, right: "four", wrong: "five" },
  ];
  const a = (key: string, params: Record<string, string | number> = {}) => t(`assessmentAuthoring.${key}`, params);

  await signIn(page, "staff", cls.teacher.email, cls.teacher.password, /\/teach/);

  await test.step("the teacher creates a bank with two single-choice questions", async () => {
    await page.goto("/teach/assessments/banks");
    const sheet = await openSheet(page, a("banks.new"));
    await sheet.getByLabel(a("banks.name"), { exact: true }).fill(bankName);
    await fieldSelect(sheet, a("common.language")).selectOption("en");
    await sheet.getByRole("button", { name: a("common.save"), exact: true }).click();
    await page.waitForURL(/\/teach\/assessments\/banks\/[0-9a-f-]+$/);
    for (const q of questions) {
      const form = await openSheet(page, a("question.new"));
      await form.getByLabel(a("question.stem"), { exact: true }).fill(q.stem);
      await form.getByLabel(a("question.optionN", { n: "A" }), { exact: true }).fill(q.right);
      await form.getByLabel(a("question.optionN", { n: "B" }), { exact: true }).fill(q.wrong);
      await form.getByRole("radio", { name: a("question.correct") }).first().check();
      await form.getByRole("button", { name: a("common.save"), exact: true }).click();
      await expect(page.getByRole("dialog")).toHaveCount(0);
      await expect(page.getByText(q.stem.split(":")[0]!).first()).toBeVisible();
    }
  });

  await test.step("the teacher builds a paper from the two questions and assigns it to the class as a quiz", async () => {
    await page.goto("/teach/assessments/papers/new");
    await (await hydrated(page.getByLabel(a("papers.paperTitle"), { exact: true }))).fill(paperTitle);
    await fieldSelect(page, a("common.language")).selectOption("en");
    const bankSelect = fieldSelect(page, a("items.bank"));
    await expect(bankSelect.locator("option", { hasText: bankName })).toHaveCount(1);
    await bankSelect.selectOption((await bankSelect.locator("option", { hasText: bankName }).getAttribute("value"))!);
    const questionSelect = fieldSelect(page, a("items.question"));
    for (const q of questions) {
      const option = questionSelect.locator("option", { hasText: q.stem.split(":")[0]! });
      await expect(option).toHaveCount(1);
      await questionSelect.selectOption((await option.getAttribute("value"))!);
      await page.getByRole("button", { name: a("items.addButton"), exact: true }).click();
    }
    await page.getByRole("button", { name: a("common.save"), exact: true }).first().click();
    await page.waitForURL(/\/teach\/assessments\/papers\/[0-9a-f-]+$/);
    await page.getByRole("link", { name: a("papers.assign") }).click();
    await page.waitForURL(/\/teach\/assessments\/assign/);
    await (await hydrated(fieldSelect(page, t("assessment.assign.class")))).selectOption({ label: cls.code });
    await page.waitForURL(/class=/);
    await (await hydrated(fieldSelect(page, t("assessment.assign.kind")))).selectOption("Quiz");
    await page.getByLabel(t("assessment.assign.timeLimit")).fill("10");
    await page.getByRole("button", { name: t("assessment.assign.submit") }).click();
    await page.waitForURL(/\/teach\/assessments\/assignments\/[0-9a-f-]+$/);
    await expect(page.getByRole("heading", { name: paperTitle })).toBeVisible();
  });

  const learnerContext = await browser.newContext();
  const learnerPage = await learnerContext.newPage();
  await test.step("the learner starts it: the server clock runs, an answer is saved and still there after a reload", async () => {
    await signIn(learnerPage, "learn", learner.username, learner.password);
    await learnerPage.goto("/learn/assessments");
    await learnerPage.getByRole("link").filter({ hasText: paperTitle }).click();
    await (await hydrated(learnerPage.getByRole("button", { name: t("assessment.learn.start") }))).click();
    await learnerPage.waitForURL(/\/learn\/attempts\/[0-9a-f-]+$/);
    await expect(learnerPage.getByLabel(t("assessment.player.timeLeft"))).toHaveText(/0?9:\d\d|10:00/);

    const saved = learnerPage.waitForResponse((r) => r.url().endsWith("/answers") && r.request().method() === "PUT" && r.ok());
    await (await hydrated(learnerPage.locator("label").filter({ hasText: questions[0]!.right }))).click();
    await saved;
    await learnerPage.reload();
    await expect(learnerPage.locator("label").filter({ hasText: questions[0]!.right }).locator("input")).toBeChecked();
  });

  await test.step("the learner answers the second question wrong, submits and sees the score of the machine grading", async () => {
    await (await hydrated(learnerPage.getByRole("button", { name: t("assessment.player.next") }))).click();
    await learnerPage.locator("label").filter({ hasText: questions[1]!.wrong }).click();
    await learnerPage.getByRole("button", { name: t("assessment.player.submit") }).click();
    await learnerPage.getByRole("button", { name: t("assessment.player.confirm") }).click();
    await learnerPage.waitForURL(/\/result$/);
    await expect(learnerPage.getByText(t("assessment.learn.scoreValue", { score: 1, max: 2 }), { exact: true }).first()).toBeVisible();
  });
  await learnerContext.close();
});
