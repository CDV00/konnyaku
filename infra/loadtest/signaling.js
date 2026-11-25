// Load on the classroom signaling service alone (k6): ROOMS rooms of ROOM_SIZE people, each joins with a ticket
// signed here with SIGNALING_SECRET, then sends a state change and one signal per peer every 2 s for HOLD seconds.
//   docker run --rm --network <net> -v "$PWD/infra/loadtest:/s:ro" grafana/k6:0.57.0 run \
//     -e URL=ws://signaling:5100/rtc/ws -e SECRET=<SIGNALING_SECRET> -e ROOMS=50 /s/signaling.js
import ws from "k6/ws";
import crypto from "k6/crypto";
import encoding from "k6/encoding";
import { check } from "k6";

const URL = __ENV.URL || "ws://signaling:5100/rtc/ws";
const SECRET = __ENV.SECRET;
const ROOMS = Number(__ENV.ROOMS || 50);
const SIZE = Number(__ENV.ROOM_SIZE || 6);
const HOLD = Number(__ENV.HOLD || 60);

export const options = {
  scenarios: { rooms: { executor: "per-vu-iterations", vus: ROOMS * SIZE, iterations: 1, maxDuration: `${HOLD + 60}s` } },
};

const b64 = (s) => encoding.b64encode(s, "rawurl");
const tenant = "11111111-1111-4111-8111-111111111111";
const uuid = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;

function ticket(vu) {
  const room = Math.floor((vu - 1) / SIZE);
  const now = Math.floor(Date.now() / 1000);
  const claims = {
    sub: uuid(vu), tid: tenant, sid: uuid(100000 + room), name: `P${vu}`,
    role: (vu - 1) % SIZE === 0 ? "host" : "learner",
    ends: now + 3600, exp: now + 120, iss: "konnyaku", aud: "konnyaku-rtc",
  };
  const body = `${b64(JSON.stringify({ alg: "HS256", typ: "JWT" }))}.${b64(JSON.stringify(claims))}`;
  const sig = encoding.b64encode(crypto.hmac("sha256", SECRET, body, "binary"), "rawurl");
  return `${body}.${sig}`;
}

export default function () {
  const res = ws.connect(URL, {}, (socket) => {
    const peers = new Set();
    socket.on("open", () => socket.send(JSON.stringify({ type: "join", ticket: ticket(__VU), state: { audio: true, video: true, screen: false } })));
    socket.on("message", (raw) => {
      const msg = JSON.parse(raw);
      if (msg.type === "welcome") msg.peers.forEach((p) => peers.add(p.id ?? p));
      if (msg.type === "peer-joined") peers.add(msg.peer.id ?? msg.peer);
      if (msg.type === "peer-left") peers.delete(msg.id);
      if (msg.type === "error") console.warn(`error ${msg.code}`);
    });
    let on = true;
    socket.setInterval(() => {
      on = !on;
      socket.send(JSON.stringify({ type: "state", audio: on, video: true, screen: false }));
      // ~1 KB, the size of an ICE candidate burst / small SDP.
      peers.forEach((p) => socket.send(JSON.stringify({ type: "signal", to: p, data: { sdp: "x".repeat(1000) } })));
    }, 2000);
    socket.setTimeout(() => { socket.send(JSON.stringify({ type: "leave" })); socket.close(); }, HOLD * 1000);
  });
  check(res, { "upgraded 101": (r) => r && r.status === 101 });
}
