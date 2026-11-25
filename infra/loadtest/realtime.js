// Load on the realtime sockets (/rt) of the signaling service (k6): TABS open tabs of USERS people (tickets signed
// here with SIGNALING_SECRET), held HOLD seconds. Signals come from Redis meanwhile, e.g. a publisher loop:
//   docker run --rm --network <net> -v "$PWD/infra/loadtest:/s:ro" grafana/k6:0.57.0 run \
//     -e URL=ws://signaling:5100/rt -e SECRET=<SIGNALING_SECRET> -e TABS=1000 /s/realtime.js
//   while true; do redis-cli publish "user:00000000-0000-4000-8000-$(printf %012d $((RANDOM % USERS + 1)))" \
//     '{"t":"inbox","id":"0193","seq":1}'; done
import ws from "k6/ws";
import crypto from "k6/crypto";
import encoding from "k6/encoding";
import { check } from "k6";
import { Counter } from "k6/metrics";

const URL = __ENV.URL || "ws://signaling:5100/rt";
const SECRET = __ENV.SECRET;
const TABS = Number(__ENV.TABS || 1000);
const USERS = Number(__ENV.USERS || Math.ceil(TABS / 2));
const HOLD = Number(__ENV.HOLD || 60);
const signals = new Counter("realtime_signals");

export const options = {
  scenarios: { tabs: { executor: "per-vu-iterations", vus: TABS, iterations: 1, maxDuration: `${HOLD + 60}s` } },
};

const b64 = (s) => encoding.b64encode(s, "rawurl");
const uuid = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;

function ticket(vu) {
  const now = Math.floor(Date.now() / 1000);
  const claims = { sub: uuid(((vu - 1) % USERS) + 1), tid: "11111111-1111-4111-8111-111111111111", exp: now + 60, iss: "konnyaku", aud: "konnyaku-realtime" };
  const body = `${b64(JSON.stringify({ alg: "HS256", typ: "JWT" }))}.${b64(JSON.stringify(claims))}`;
  const sig = encoding.b64encode(crypto.hmac("sha256", SECRET, body, "binary"), "rawurl");
  return `${body}.${sig}`;
}

export default function () {
  const res = ws.connect(URL, {}, (socket) => {
    socket.on("open", () => socket.send(JSON.stringify({ type: "auth", ticket: ticket(__VU) })));
    socket.on("message", (raw) => {
      const msg = JSON.parse(raw);
      if (msg.t === "error") console.warn(`error ${msg.code}`);
      if (msg.t === "inbox") signals.add(1);
    });
    socket.setTimeout(() => socket.close(), HOLD * 1000);
  });
  check(res, { "upgraded 101": (r) => r && r.status === 101 });
}
