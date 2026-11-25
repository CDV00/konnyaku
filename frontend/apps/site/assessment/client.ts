type Method = "GET" | "POST" | "PUT" | "DELETE";

/** Browser-side call of a relay: JSON in, JSON out, `{ ok: false, code }` on an API error. */
export async function callApi<T>(url: string, method: Method = "GET", body?: unknown): Promise<{ ok: true; data: T } | { ok: false; code: string; params: Record<string, unknown> }> {
  try {
    const response = await fetch(url, {
      method,
      headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const parsed = (await response.json().catch(() => ({}))) as { code?: string; params?: Record<string, unknown> };
    if (response.ok) return { ok: true, data: parsed as T };
    return { ok: false, code: parsed.code ?? "unknown", params: parsed.params ?? {} };
  } catch {
    return { ok: false, code: "network", params: {} };
  }
}
