/**
 * Personal data never leaves the server in traces or error reports: query strings (search boxes hold names and phone
 * numbers), client addresses and user agents are dropped; emails, phone numbers, tokens and passwords quoted in error
 * messages are masked. Same rules as PersonalDataScrubber of the API.
 */

const patterns: [RegExp, string][] = [
  [/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, "[email]"],
  [/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g, "[token]"],
  [/Bearer\s+[A-Za-z0-9._~+/-]+=*/gi, "Bearer [token]"],
  [/\b(password|pwd|secret|token|api[_-]?key)\s*[=:]\s*[^;\s,"']+/gi, "$1=[redacted]"],
  [/(?<![\w-])(?:\+\d{1,3}|0)(?:[\s.-]?\d){8,11}(?!\d)/g, "[phone]"],
];

export function scrubText(text: string): string;
export function scrubText(text: string | undefined): string | undefined;
export function scrubText(text: string | undefined): string | undefined {
  if (!text) return text;
  return patterns.reduce((value, [pattern, replacement]) => value.replace(pattern, replacement), text);
}

/** "/api/crm/leads?search=Lan" → "/api/crm/leads"; works for absolute URLs and span names ("fetch GET http://…?q"). */
export function withoutQuery(value: string): string {
  return value.replace(/\?[^\s]*/g, "");
}

/** Span attributes that identify a visitor or carry a query string. */
export const droppedAttributes = [
  "http.client_ip",
  "client.address",
  "net.peer.ip",
  "network.peer.address",
  "http.user_agent",
  "user_agent.original",
  "url.query",
];

/** Attributes whose value is a URL or path that may end with a query string. */
export const urlAttributes = ["http.url", "http.target", "url.full", "next.span_name"];
