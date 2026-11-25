// Preloaded by the web image (node --require, see Dockerfile) with NEXT_MANUAL_SIG_HANDLE set: Next.js' own handler
// exits on SIGTERM at once, cutting the requests in flight. During a rolling deploy (infra/deploy/scripts/deploy.sh)
// the replica being replaced instead:
//   1. keeps serving for SHUTDOWN_DRAIN_MS (5 s), answering with "Connection: close" so every connection ends after a
//      complete response, while idle ones are closed by Caddy itself (its upstream keep-alive is 3 s, Caddyfile);
//   2. stops accepting connections (Caddy retries a refused connection on another replica), finishes what is in flight
//      and exits 0 once every connection is closed — at the latest SHUTDOWN_TIMEOUT_MS (25 s) after the signal
//      (stop_grace_period is 30 s).
// The server never closes a connection itself: Node counts a just-accepted connection whose request is not read yet
// as idle, and closing it would fail a request Caddy cannot retry (a POST).
"use strict";
const http = require("node:http");

const servers = new Set();
let draining = false;
const listen = http.Server.prototype.listen;
http.Server.prototype.listen = function (...args) {
  if (!servers.has(this)) {
    servers.add(this);
    this.prependListener("request", (_request, response) => {
      if (draining) response.setHeader("Connection", "close");
    });
  }
  return listen.apply(this, args);
};

function shutdown(signal) {
  if (draining) return;
  draining = true;
  const drain = Number(process.env.SHUTDOWN_DRAIN_MS ?? 5000);
  const timeout = Number(process.env.SHUTDOWN_TIMEOUT_MS ?? 25000);
  console.log(`${signal}: draining for ${drain} ms, then closing ${servers.size} server(s)`);
  setTimeout(() => {
    console.log(`${signal}: timeout after ${timeout} ms, exiting`);
    process.exit(0);
  }, timeout).unref();
  setTimeout(() => {
    let open = servers.size;
    if (open === 0) process.exit(0);
    for (const server of servers) {
      // server.close() also closes "idle" connections (Node >= 19), new ones included: keep them.
      server.closeIdleConnections = () => {};
      server.close(() => {
        open -= 1;
        if (open === 0) process.exit(0);
      });
    }
  }, drain);
}

process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));
