// Mixed load on the production stack through Caddy (k6). Reproduces the numbers in docs/toi-uu-tai-nguyen.md.
// Needs the demo tenant (infra/scripts/seed_demo.py) and, for the learner, its temporary password:
//   docker run --rm --network <project>_edge -v "$PWD/infra/loadtest:/s" grafana/k6:0.57.0 run \
//     -e BASE=http://caddy -e LEARNER_TEMP=<temporary password> -e PROFILE=load /s/mixed.js
// PROFILE=load: 10 visitors + 5 staff + 5 learners + 2 sign-ins in a loop, 1 s think time (about 40-50 req/s).
// PROFILE=stress: the same mix with 4x the users and no think time (finds the ceiling of the machine).
import http from "k6/http";
import { check, sleep } from "k6";

const BASE = __ENV.BASE || "http://caddy";
const HOST = __ENV.TENANT_HOST || "demo.localhost";
const PROFILE = __ENV.PROFILE || "load";
const DURATION = __ENV.DURATION || "2m";
const scale = PROFILE === "stress" ? 4 : 1;
const think = PROFILE === "stress" ? 0 : 1;

const STAFF = { email: "admin@demo.example.com", password: "Admin-Pass-123" };
const TEACHER = { email: "teacher@demo.example.com", password: "Teacher-Pass-123" };
const LEARNER_PASSWORD = "Learner-Load-123";

export const options = {
  scenarios: {
    visitor: { executor: "constant-vus", vus: 10 * scale, duration: DURATION, exec: "visitor" },
    staff: { executor: "constant-vus", vus: 5 * scale, duration: DURATION, exec: "staff" },
    learner: { executor: "constant-vus", vus: 5 * scale, duration: DURATION, exec: "learner" },
    signin: { executor: "constant-vus", vus: 2 * scale, duration: DURATION, exec: "signin" },
  },
  // Per-page latency: the summary prints p(95) for every `page` tag.
  thresholds: {
    "http_req_duration{page:home}": ["p(95)>=0"],
    "http_req_duration{page:register}": ["p(95)>=0"],
    "http_req_duration{page:lead}": ["p(95)>=0"],
    "http_req_duration{page:login}": ["p(95)>=0"],
    "http_req_duration{page:ops}": ["p(95)>=0"],
    "http_req_duration{page:ops_list}": ["p(95)>=0"],
    "http_req_duration{page:learn}": ["p(95)>=0"],
    http_req_failed: ["rate<0.01"],
  },
  summaryTrendStats: ["avg", "med", "p(95)", "p(99)", "max"],
};

const headers = (extra = {}) => ({ Host: HOST, ...extra });

// Session cookies are `Secure` in production, so k6's jar would not send them over plain HTTP: carry them by hand.
function signIn(realm, body) {
  const res = http.post(`${BASE}/api/session/${realm}`, JSON.stringify(body), {
    headers: headers({ "Content-Type": "application/json" }),
    tags: { page: "login" },
    jar: null,
  });
  if (!check(res, { "sign-in ok": (r) => r.status === 200 })) console.warn(`unexpected login ${res.status} ${res.error}`);
  const name = realm === "learn" ? "kk_learn" : "kk_staff";
  const cookie = res.cookies[name] && res.cookies[name][0];
  return cookie ? `${name}=${cookie.value}` : "";
}

function page(path, tag, cookie) {
  const res = http.get(`${BASE}${path}`, { headers: headers(cookie ? { Cookie: cookie } : {}), tags: { page: tag }, redirects: 0 });
  if (!check(res, { [`${tag} 200`]: (r) => r.status === 200 })) console.warn(`unexpected ${tag} ${res.status} ${res.error}`);
  return res;
}

export function setup() {
  // The seeded learner must change the temporary password once; later runs sign in with LEARNER_PASSWORD directly.
  const temp = __ENV.LEARNER_TEMP;
  if (temp) {
    const cookie = signIn("learn", { username: "hv000001", password: temp });
    if (cookie) {
      http.post(`${BASE}/learn/api/password`, JSON.stringify({ currentPassword: temp, newPassword: LEARNER_PASSWORD }), {
        headers: headers({ "Content-Type": "application/json", Cookie: cookie }),
      });
    }
  }
}

export function visitor() {
  page("/vi", "home");
  page("/vi/dang-ky", "register");
  const id = `${__VU}-${__ITER}-${Date.now()}`;
  const res = http.post(`${BASE}/api/leads`, JSON.stringify({
    studentName: `Load ${id}`, birthYear: 2012, guardianName: "Phu huynh Load", phone: null, email: `load-${id}@example.com`,
    languageCode: "ja", goal: null, preferredSchedule: null, consentGiven: true, consentVersion: "2025-01",
    locale: "vi", utm: {}, website: null,
  }), { headers: headers({ "Content-Type": "application/json" }), tags: { page: "lead" } });
  if (!check(res, { "lead 202": (r) => r.status === 202 })) console.warn(`unexpected lead ${res.status} ${res.error}`);
  sleep(think);
}

let staffCookie = "";
export function staff() {
  if (!staffCookie) staffCookie = signIn("staff", STAFF);
  page("/ops", "ops", staffCookie);
  page("/ops/classes", "ops_list", staffCookie);
  page("/ops/students", "ops_list", staffCookie);
  page("/ops/leads", "ops_list", staffCookie);
  sleep(think);
}

let learnerCookie = "";
export function learner() {
  if (!learnerCookie) learnerCookie = signIn("learn", { username: "hv000001", password: LEARNER_PASSWORD });
  page("/learn", "learn", learnerCookie);
  sleep(think);
}

// Password verification (PBKDF2) is the most CPU-heavy request of the app.
export function signin() {
  signIn("staff", TEACHER);
  sleep(think);
}
