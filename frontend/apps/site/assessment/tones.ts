import type { Tone } from "@konnyaku/ui/kk/blocks";
import type { AttemptStatus, LearnerState } from "./types";

export const stateTone: Record<LearnerState, Tone> = {
  Upcoming: "muted",
  Open: "primary",
  InProgress: "warning",
  Submitted: "highlight",
  Graded: "success",
  Closed: "muted",
};

export const attemptTone: Record<AttemptStatus, Tone> = { InProgress: "warning", Submitted: "highlight", Graded: "success" };
