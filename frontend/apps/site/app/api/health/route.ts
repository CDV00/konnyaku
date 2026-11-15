/** Liveness for the container healthcheck: this process serves requests (the API is checked separately). */
export function GET() {
  return Response.json({ status: "ok" });
}
