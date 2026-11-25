// Steady light load through Caddy while infra/deploy/scripts/deploy.sh rolls a new version: every failed request is
// printed with its time, so it can be matched with the deploy log. Expected result: 0 failed requests.
// Needs the review tenant (infra/scripts/seed_review.py) and, for the lead form, CAPTCHA_ENABLED=false on the stack
// plus a high RateLimits__PublicFormsPerMinute (every request comes from one IP).
//   docker run --rm --network <project>_edge -v "$PWD/infra/loadtest:/s" grafana/k6:0.57.0 run \
//     -e BASE=http://caddy -e DURATION=5m /s/deploy.js
// RATE: page views per second (default 20); leads and sign-ins add about RATE / 5.
import http from "k6/http";
import { check } from "k6";

const BASE = __ENV.BASE || "http://caddy";
const HOST = __ENV.TENANT_HOST || "review.localhost";
const DURATION = __ENV.DURATION || "5m";
const RATE = Number(__ENV.RATE || 20);
const STAFF = { email: "manager@review.example.com", password: "Review-Pass-123" };
const LEARNER = { username: "hv-review01", password: "Learner-Pass-123" };

const arrival = (rate, exec) => ({
  executor: "constant-arrival-rate", rate, timeUnit: "1s", duration: DURATION, preAllocatedVUs: 20, maxVUs: 100, exec,
});

export const options = {
  scenarios: {
    pages: arrival(RATE, "pages"),
    leads: arrival(Math.max(1, Math.round(RATE / 10)), "lead"),
    signin: arrival(Math.max(1, Math.round(RATE / 10)), "signin"),
  },
  thresholds: { http_req_failed: ["rate==0"] },
  summaryTrendStats: ["avg", "med", "p(95)", "p(99)", "max"],
};

const headers = (extra = {}) => ({ Host: HOST, ...extra });

function report(res, name, ok) {
  if (!check(res, { [name]: () => ok })) {
    console.warn(`FAILED ${new Date().toISOString()} ${name} status=${res.status} error=${res.error || ""}`);
  }
}

// Session cookies are `Secure` in production, so k6's jar would not send them over plain HTTP: carry them by hand.
function signIn(realm, body) {
  const res = http.post(`${BASE}/api/session/${realm}`, JSON.stringify(body), {
    headers: headers({ "Content-Type": "application/json" }), tags: { page: "login" }, jar: null,
  });
  report(res, `sign-in ${realm}`, res.status === 200);
  const name = realm === "learn" ? "kk_learn" : "kk_staff";
  const cookie = res.cookies[name] && res.cookies[name][0];
  return cookie ? `${name}=${cookie.value}` : "";
}

export function setup() {
  return { staff: signIn("staff", STAFF), learner: signIn("learn", LEARNER) };
}

const pageList = [
  ["/vi", "home", null],
  ["/vi/dang-ky", "register", null],
  ["/ops", "ops", "staff"],
  ["/ops/classes", "ops_list", "staff"],
  ["/ops/students", "ops_list", "staff"],
  ["/learn", "learn", "learner"],
];

export function pages(cookies) {
  const [path, tag, who] = pageList[Math.floor(Math.random() * pageList.length)];
  const res = http.get(`${BASE}${path}`, {
    headers: headers(who ? { Cookie: cookies[who] } : {}), tags: { page: tag }, redirects: 0,
  });
  report(res, `${tag} 200`, res.status === 200);
}

export function lead() {
  const id = `${__VU}-${__ITER}-${Date.now()}`;
  const res = http.post(`${BASE}/api/leads`, JSON.stringify({
    studentName: `Deploy ${id}`, birthYear: 2012, guardianName: "Phu huynh Deploy", phone: null,
    email: `deploy-${id}@example.com`, languageCode: "ja", goal: null, preferredSchedule: null, consentGiven: true,
    consentVersion: "2025-01", locale: "vi", utm: {}, website: null,
  }), { headers: headers({ "Content-Type": "application/json" }), tags: { page: "lead" } });
  report(res, "lead 202", res.status === 202);
}

export function signin() {
  signIn("staff", STAFF);
}
