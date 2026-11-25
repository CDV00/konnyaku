/** Shapes of the Assessment API (`/api/assessment/*`), shared by the teach, learn and ops screens. */

export const questionTypes = ["SingleChoice", "MultipleChoice", "TrueFalse", "FillBlank", "Matching", "Ordering", "ShortAnswer", "Essay"] as const;
export type QuestionType = (typeof questionTypes)[number];

export const assignmentKinds = ["Homework", "Quiz", "Test", "MockExam", "Placement"] as const;
export type AssignmentKind = (typeof assignmentKinds)[number];

export const latePolicies = ["Accept", "Penalty", "Lock"] as const;
export const scoringRules = ["Best", "Last", "Average"] as const;
/** `Peer` exists in the API but is not offered yet. */
export const graderModes = ["Auto", "Teacher"] as const;
export const scoreReleases = ["OnSubmit", "AfterGrading", "AtTime"] as const;
export const answerVisibilities = ["Never", "AfterSubmit", "AfterDue"] as const;

export type ChoiceOption = { id: string; html: string };
export type EssayLimits = { min?: number | null; max?: number | null; count: "Words" | "Characters" };
export type QuestionContent = { options: ChoiceOption[]; targets: ChoiceOption[]; blanks: number; essay?: EssayLimits | null; shuffle: boolean };
export type AnswerKey = {
  choices: string[];
  order: string[];
  pairs: Record<string, string>;
  blanks: string[][];
  accepted: string[];
  ignoreCase: boolean;
  ignoreDiacritics: boolean;
};
export type ItemResponse = { choices: string[]; order: string[]; pairs: Record<string, string>; blanks: string[]; text?: string | null };

export const emptyResponse = (): ItemResponse => ({ choices: [], order: [], pairs: {}, blanks: [], text: null });

export type Bank = {
  id: string;
  name: string;
  languageCode: string;
  programId: string | null;
  visibility: "Private" | "Shared";
  description: string | null;
  ownerId: string;
  questionCount: number;
  canEdit: boolean;
  updatedAt: string;
};
export type Group = { id: string; bankId: string; title: string; passage: string; questionCount: number };
export type Question = {
  id: string;
  bankId: string;
  groupId: string | null;
  type: QuestionType;
  stem: string;
  content: QuestionContent;
  key: AnswerKey;
  points: number;
  frameworkCode: string | null;
  skillCode: string | null;
  levelCode: string | null;
  tags: string[];
  explanation: string | null;
  version: number;
  archived: boolean;
  updatedAt: string;
};
export type BankDetail = { bank: Bank; groups: Group[]; questions: Question[] };

export type RubricCriterion = { id: string; name: string; maxBand: number };
export type Rubric = { id: string; name: string; criteria: RubricCriterion[] };
export type SectionItem = {
  kind: "Question" | "Group" | "Random";
  questionId?: string | null;
  groupId?: string | null;
  bankId?: string | null;
  tags: string[];
  skillCode?: string | null;
  levelCode?: string | null;
  count: number;
  points?: number | null;
  rubricId?: string | null;
};
export type PaperSection = { id: string; title: string; skillCode?: string | null; timeLimitMinutes?: number | null; shuffleQuestions: boolean; items: SectionItem[] };
export type PaperLayout = { sections: PaperSection[]; rubrics: Rubric[] };
export type Paper = {
  id: string;
  title: string;
  languageCode: string;
  frameworkCode: string | null;
  description: string | null;
  showRuby: boolean;
  layout: PaperLayout;
  ownerId: string;
  canEdit: boolean;
  questionCount: number;
  totalMinutes: number | null;
  archived: boolean;
  updatedAt: string;
};
export type PaperDetail = { paper: Paper; questions: Question[]; groups: Group[] };

export type AssignmentStats = { students: number; started: number; submitted: number; graded: number; awaitingGrading: number };
export type Assignment = {
  id: string;
  paperId: string;
  paperTitle: string;
  title: string;
  target: "Class" | "Session" | "Students";
  classId: string;
  classCode: string;
  branchId: string;
  sessionId: string | null;
  studentIds: string[];
  kind: AssignmentKind;
  opensAt: string;
  dueAt: string | null;
  latePolicy: (typeof latePolicies)[number];
  latePenaltyPercent: number;
  maxAttempts: number | null;
  scoringRule: (typeof scoringRules)[number];
  grader: "Auto" | "Teacher" | "Peer";
  scoreRelease: (typeof scoreReleases)[number];
  releaseAt: string | null;
  answerVisibility: (typeof answerVisibilities)[number];
  timeLimitMinutes: number | null;
  createdBy: string;
  createdAt: string;
  stats: AssignmentStats;
};
export type AttemptStatus = "InProgress" | "Submitted" | "Graded";
export type StudentAttemptRow = {
  studentId: string;
  studentName: string;
  attempts: number;
  lastStatus: AttemptStatus | null;
  lastAttemptId: string | null;
  lastSubmittedAt: string | null;
  late: boolean;
  finalScore: number | null;
  maxScore: number | null;
  pendingItems: number;
  flags: number;
};
export type AssignmentDetail = { assignment: Assignment; students: StudentAttemptRow[] };
export type QueueItem = {
  attemptId: string;
  assignmentId: string;
  assignmentTitle: string;
  kind: AssignmentKind;
  classId: string;
  classCode: string;
  studentId: string;
  studentName: string;
  number: number;
  submittedAt: string | null;
  dueAt: string | null;
  late: boolean;
  pendingItems: number;
  rawScore: number;
  maxScore: number;
};

export type AttemptSection = { title: string; skillCode: string | null; timeLimitMinutes: number | null };
export type PlayItem = {
  id: string;
  ordinal: number;
  sectionIndex: number;
  locked: boolean;
  type: QuestionType;
  stem: string;
  options: ChoiceOption[];
  targets: ChoiceOption[];
  blanks: number;
  essay: EssayLimits | null;
  points: number;
  groupId: string | null;
  groupTitle: string | null;
  passage: string | null;
  response: ItemResponse;
  flagged: boolean;
};
export type AttemptPlay = {
  id: string;
  assignmentId: string;
  title: string;
  kind: AssignmentKind;
  number: number;
  status: AttemptStatus;
  startedAt: string;
  deadlineAt: string | null;
  sectionIndex: number;
  sectionDeadlineAt: string | null;
  sections: AttemptSection[];
  timed: boolean;
  showRuby: boolean;
  serverNow: string;
  items: PlayItem[];
};
export type ItemGradeState = "Open" | "Auto" | "NeedsTeacher" | "Teacher";
export type ResultItem = {
  id: string;
  ordinal: number;
  sectionIndex: number;
  type: QuestionType;
  stem: string;
  options: ChoiceOption[];
  targets: ChoiceOption[];
  blanks: number;
  essay: EssayLimits | null;
  points: number;
  groupTitle: string | null;
  passage: string | null;
  skillCode: string | null;
  response: ItemResponse;
  flagged: boolean;
  state: ItemGradeState | null;
  score: number | null;
  correct: boolean | null;
  key: AnswerKey | null;
  explanation: string | null;
  comment: string | null;
  rubric: Rubric | null;
  bands: Record<string, number> | null;
  questionVersion: number;
};
export type SkillScore = { skillCode: string; score: number; maxScore: number };
export type AttemptResult = {
  id: string;
  assignmentId: string;
  title: string;
  kind: AssignmentKind;
  number: number;
  status: AttemptStatus;
  startedAt: string;
  submittedAt: string | null;
  late: boolean;
  autoSubmitted: boolean;
  scoresVisible: boolean;
  answersVisible: boolean;
  releaseAt: string | null;
  score: number | null;
  rawScore: number | null;
  maxScore: number;
  penaltyPercent: number;
  pendingItems: number;
  comment: string | null;
  skills: SkillScore[];
  showRuby: boolean;
  sections: AttemptSection[];
  items: ResultItem[];
};
export type AttemptEvent = { kind: "TabHidden" | "Paste"; itemId: string | null; value: number; occurredAt: string };
export type AttemptReview = {
  id: string;
  assignmentId: string;
  assignmentTitle: string;
  classId: string;
  studentId: string;
  studentName: string;
  number: number;
  status: AttemptStatus;
  startedAt: string;
  submittedAt: string | null;
  late: boolean;
  autoSubmitted: boolean;
  score: number | null;
  rawScore: number;
  maxScore: number;
  penaltyPercent: number;
  pendingItems: number;
  comment: string | null;
  grader: "Auto" | "Teacher" | "Peer";
  skills: SkillScore[];
  sections: AttemptSection[];
  items: ResultItem[];
  events: AttemptEvent[];
};

export type LearnerState = "Upcoming" | "Open" | "InProgress" | "Submitted" | "Graded" | "Closed";
export type MyAssignment = {
  id: string;
  title: string;
  kind: AssignmentKind;
  classId: string;
  opensAt: string;
  dueAt: string | null;
  latePolicy: string;
  maxAttempts: number | null;
  attemptsUsed: number;
  timeLimitMinutes: number | null;
  state: LearnerState;
  openAttemptId: string | null;
  lastAttemptId: string | null;
  scoreVisible: boolean;
  score: number | null;
  maxScore: number | null;
  canStart: boolean;
};
export type MyAttemptSummary = {
  id: string;
  number: number;
  status: AttemptStatus;
  startedAt: string;
  submittedAt: string | null;
  late: boolean;
  scoreVisible: boolean;
  score: number | null;
  maxScore: number;
};
export type MyAssignmentDetail = {
  assignment: MyAssignment;
  scoringRule: string;
  scoreRelease: string;
  releaseAt: string | null;
  answerVisibility: string;
  paperMinutes: number | null;
  paperQuestions: number;
  attempts: MyAttemptSummary[];
};

export type Overview = {
  assignments: number;
  openNow: number;
  awaitingGrading: number;
  gradedLast30Days: number;
  averagePercent: number | null;
  byKind: { kind: AssignmentKind; count: number }[];
  recent: Assignment[];
};

/** Catalog frameworks (skills and levels of a language). */
export type Framework = {
  code: string;
  name: Record<string, string>;
  languageCode: string | null;
  levels: { code: string; ordinal: number }[];
  skills: { code: string; name: Record<string, string> }[];
};

/** API error code → i18n key (`assessment.not_open` → `assessment_not_open`). */
export const errorKey = (code: string | undefined) => (code ?? "unknown").replaceAll(".", "_");
