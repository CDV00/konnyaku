import { assessmentRelay, route } from "@/assessment/relay";

/** The learner's own assignments and attempts (`/api/assessment/my/*`). */
export const { GET, POST, PUT, DELETE } = assessmentRelay("learn", "/api/assessment/my", {
  GET: [route("assignments"), route("assignments/{id}"), route("attempts/{id}"), route("attempts/{id}/result")],
  POST: [route("assignments/{id}/attempts"), route("attempts/{id}/(events|next-section|submit)")],
  PUT: [route("attempts/{id}/answers")],
  DELETE: [],
});
