import { assessmentRelay, route } from "@/assessment/relay";

/** Teacher screens of the Assessment module (banks, papers, assignments, grading); the API checks ownership and classes. */
export const { GET, POST, PUT, DELETE } = assessmentRelay("staff", "/api/assessment", {
  GET: [
    route("banks"),
    route("banks/{id}"),
    route("papers"),
    route("papers/{id}"),
    route("assignments"),
    route("assignments/{id}"),
    route("grading"),
    route("classes/{id}/students"),
    route("attempts/{id}"),
  ],
  POST: [
    route("banks"),
    route("banks/{id}/(groups|questions)"),
    route("questions/{id}/(archive|restore)"),
    route("papers"),
    route("papers/{id}/(archive|restore)"),
    route("assignments"),
    route("assignments/{id}/release"),
    route("attempts/{id}/finalize"),
  ],
  PUT: [
    route("banks/{id}"),
    route("groups/{id}"),
    route("questions/{id}"),
    route("papers/{id}"),
    route("assignments/{id}"),
    route("attempts/{id}/items/{id}/grade"),
  ],
  DELETE: [route("assignments/{id}")],
});
