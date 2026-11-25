#!/usr/bin/env node
// One command for the end-to-end tests: a throwaway stack (PostgreSQL + Mailpit + Redis + the realtime service in Docker,
// Api, Worker, web), the review seed, then Playwright. Same script locally and in CI (`--infra=external` when the CI job
// runs PostgreSQL, Mailpit and Redis as services and builds the realtime service itself: E2E_REALTIME_BIN).
//
//   node frontend/e2e/run.mjs                    build, start everything, seed, test, tear down
//   node frontend/e2e/run.mjs --no-build         reuse the last Release build of the backend and .next of the web
//   node frontend/e2e/run.mjs --serve            start and seed, then keep running (Ctrl+C stops): run `pnpm test` against it
//   node frontend/e2e/run.mjs -- --grep refund   everything after `--` goes to `playwright test`
//
// Ports (E2E_* variables) avoid the dev stack (5433 / 5080 / 4000 / 1025 / 8025). See docs/chay-local.md.
import { spawn, spawnSync } from "node:child_process";
import { createWriteStream, mkdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "../..");
const isWindows = process.platform === "win32";

const argv = process.argv.slice(2);
const dashDash = argv.indexOf("--");
const own = dashDash === -1 ? argv : argv.slice(0, dashDash);
const playwrightArgs = dashDash === -1 ? [] : argv.slice(dashDash + 1);
const flag = (name) => own.includes(name);
const option = (name, fallback) => own.find((a) => a.startsWith(`${name}=`))?.slice(name.length + 1) ?? fallback;

const ports = {
  pg: process.env.E2E_PG_PORT ?? "5450",
  smtp: process.env.E2E_SMTP_PORT ?? "1030",
  mailpit: process.env.E2E_MAILPIT_PORT ?? "8030",
  api: process.env.E2E_API_PORT ?? "5190",
  web: process.env.E2E_WEB_PORT ?? "4950",
  redis: process.env.E2E_REDIS_PORT ?? "6390",
  rt: process.env.E2E_RT_PORT ?? "5195",
};
// Secret shared by the API (Rtc:SignalingSecret of appsettings.Development.json) and the realtime service.
const realtimeSecret = "dev-only-signaling-secret-0123456789abcdef";
const infra = option("--infra", "docker"); // docker | external
const build = !flag("--no-build");
const serve = flag("--serve");
const python = process.env.PYTHON ?? (isWindows ? "python" : "python3");
const apiUrl = `http://localhost:${ports.api}`;
const logs = join(here, "logs");
mkdirSync(logs, { recursive: true });

const compose = ["compose", "-f", join(here, "compose.yaml")];
const composeEnv = {
  ...process.env, E2E_PG_PORT: ports.pg, E2E_SMTP_PORT: ports.smtp, E2E_MAILPIT_PORT: ports.mailpit, E2E_REDIS_PORT: ports.redis, E2E_RT_PORT: ports.rt,
};

// Module connection strings of the dev settings, pointed at the e2e database (new modules are picked up automatically).
const devSettings = JSON.parse(readFileSync(join(repo, "backend/src/Hosts/Konnyaku.Api/appsettings.Development.json"), "utf8").replace(/^﻿/, ""));
const connections = Object.fromEntries(
  Object.entries(devSettings.ConnectionStrings).map(([module, value]) => [
    `ConnectionStrings__${module}`,
    value.replace(/Port=\d+/, `Port=${ports.pg}`),
  ]),
);

const backendEnv = {
  ...process.env,
  ...connections,
  ASPNETCORE_ENVIRONMENT: "Development",
  DOTNET_ENVIRONMENT: "Development",
  // Tests send public forms and password resets far faster than people do.
  Captcha__Enabled: "false",
  RateLimits__PublicFormsPerMinute: "10000",
  RateLimits__PasswordResetPerMinute: "10000",
  Notification__Smtp__Port: ports.smtp,
  Notification__SiteUrlTemplate: `http://{host}:${ports.web}`,
  // Realtime: signals through this stack's Redis, sockets to this stack's realtime service.
  Redis__ConnectionString: `localhost:${ports.redis}`,
  Realtime__Url: `ws://localhost:${ports.rt}/rt`,
  // Files: LocalDisk storage shared by Api and Worker, signed URLs pointing at this API.
  Storage__Provider: "LocalDisk",
  Storage__LocalDisk__Root: join(here, ".storage"),
  Storage__LocalDisk__PublicBaseUrl: apiUrl,
  DOTNET_CLI_TELEMETRY_OPTOUT: "1",
  DOTNET_NOLOGO: "1",
};

const children = [];
let stopping = false;

function log(message) {
  console.log(`\x1b[36m[e2e]\x1b[0m ${message}`);
}

/** Runs a command to completion (inherits the terminal); exits the script when it fails. */
function run(command, args, options = {}) {
  log(`${command} ${args.join(" ")}`);
  const result = spawnSync(command, args, { stdio: "inherit", shell: isWindows && !command.endsWith(".exe"), ...options });
  if (result.status !== 0) {
    throw new Error(`${command} ${args[0] ?? ""} failed (exit ${result.status ?? result.signal})`);
  }
}

/** Runs a command to completion with its output in logs/<name>.log (for steps that run side by side). */
function runLogged(name, command, args, options = {}) {
  const file = join(logs, `${name}.log`);
  log(`${name}: ${command} ${args.join(" ")}  (log: e2e/logs/${name}.log)`);
  return new Promise((resolvePromise, reject) => {
    const out = createWriteStream(file);
    const child = spawn(command, args, { shell: isWindows && !command.endsWith(".exe"), ...options });
    child.stdout.pipe(out);
    child.stderr.pipe(out);
    child.on("error", reject);
    child.on("close", (code) => {
      out.end(() => {
        if (code === 0) return resolvePromise();
        const tail = readFileSync(file, "utf8").split(/\r?\n/).slice(-40).join("\n");
        reject(new Error(`${name} failed (exit ${code}):\n${tail}`));
      });
    });
  });
}

/** Starts a long-running process, its output in logs/<name>.log; resolves once `ready()` says so. */
function start(name, command, args, options) {
  log(`start ${name}: ${command} ${args.join(" ")}  (log: e2e/logs/${name}.log)`);
  const out = createWriteStream(join(logs, `${name}.log`));
  const child = spawn(command, args, { ...options, detached: !isWindows, shell: false });
  child.stdout.pipe(out);
  child.stderr.pipe(out);
  child.on("exit", (code) => {
    if (!stopping) console.error(`[e2e] ${name} exited early (code ${code}); see frontend/e2e/logs/${name}.log`);
  });
  children.push({ name, child });
  return child;
}

async function waitFor(what, check, seconds) {
  const until = Date.now() + seconds * 1000;
  while (Date.now() < until) {
    if (await check().catch(() => false)) return;
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`timed out after ${seconds}s waiting for ${what}`);
}

const httpOk = (url) => () => fetch(url).then((r) => r.ok);

function stopAll() {
  stopping = true;
  for (const { child } of children.reverse()) {
    if (child.exitCode !== null) continue;
    if (isWindows) spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], { stdio: "ignore" });
    else {
      try {
        process.kill(-child.pid, "SIGTERM");
      } catch {
        /* already gone */
      }
    }
  }
}

function infraDown() {
  if (infra === "docker" && !process.env.E2E_KEEP_INFRA) {
    spawnSync("docker", [...compose, "down", "-v", "--remove-orphans"], { stdio: "ignore", env: composeEnv });
  }
}

async function main() {
  const started = Date.now();

  // Infrastructure, backend build and web build side by side.
  const steps = [];
  if (infra === "docker") {
    log("fresh PostgreSQL + Mailpit + Redis + realtime (docker compose project konnyaku-e2e)");
    spawnSync("docker", [...compose, "down", "-v", "--remove-orphans"], { stdio: "ignore", env: composeEnv });
    steps.push(runLogged("infra", "docker", [...compose, "up", "-d", "--build", "--wait"], { env: composeEnv }));
  }
  if (build) {
    // Release output: does not touch the Debug build a running dev API holds open.
    steps.push(runLogged("build-backend", "dotnet", ["build", join(repo, "backend/Konnyaku.slnx"), "-c", "Release", "--nologo"], { env: backendEnv }));
    steps.push(runLogged("build-web", process.execPath, [join(repo, "frontend/apps/site/node_modules/next/dist/bin/next"), "build"], {
      cwd: join(repo, "frontend/apps/site"),
      env: { ...process.env, NEXT_TELEMETRY_DISABLED: "1" },
      shell: false,
    }));
  }
  await Promise.all(steps);
  log(`infrastructure and builds ready in ${Math.round((Date.now() - started) / 1000)}s`);

  const dotnetRun = (project) => ["run", "--no-build", "--no-launch-profile", "-c", "Release", "--project", join(repo, "backend/src/Hosts", project)];

  await runLogged("migrate", "dotnet", dotnetRun("Konnyaku.Migrator"), {
    env: { ...backendEnv, ConnectionStrings__Owner: `Host=localhost;Port=${ports.pg};Database=konnyaku;Username=konnyaku;Password=konnyaku_dev` },
  });

  // CI (--infra=external): the realtime service is a binary built by the job; locally it runs in the compose project.
  if (infra === "external" && process.env.E2E_REALTIME_BIN) {
    start("realtime", process.env.E2E_REALTIME_BIN, [], {
      env: { ...process.env, LISTEN_ADDR: `127.0.0.1:${ports.rt}`, SIGNALING_SECRET: realtimeSecret, REDIS_URL: `redis://127.0.0.1:${ports.redis}` },
    });
  }
  start("api", "dotnet", dotnetRun("Konnyaku.Api"), { env: { ...backendEnv, ASPNETCORE_URLS: apiUrl } });
  const worker = start("worker", "dotnet", dotnetRun("Konnyaku.Worker"), { env: backendEnv });
  let workerReady = false;
  worker.stdout.on("data", (chunk) => {
    if (String(chunk).includes("Application started")) workerReady = true;
  });
  start("web", process.execPath, [join(repo, "frontend/apps/site/node_modules/next/dist/bin/next"), "start", "--port", ports.web], {
    cwd: join(repo, "frontend/apps/site"),
    env: { ...process.env, KONNYAKU_API_URL: apiUrl, NEXT_TELEMETRY_DISABLED: "1" },
  });

  await waitFor("the API", httpOk(`${apiUrl}/health`), 120);
  await waitFor("the Worker", async () => workerReady, 120);
  await waitFor("the web app", httpOk(`http://localhost:${ports.web}/api/health`), 120);
  await waitFor("the realtime service and its Redis subscription", async () => {
    const health = await fetch(`http://localhost:${ports.rt}/rt/health`).then((r) => r.json());
    return health.realtime?.redis === true;
  }, 120);

  run(python, [join(repo, "infra/scripts/seed_review.py")], { env: { ...process.env, KONNYAKU_API: apiUrl, PYTHONIOENCODING: "utf-8" }, shell: false });
  log(`stack ready in ${Math.round((Date.now() - started) / 1000)}s: http://review.localhost:${ports.web}  (Mailpit http://localhost:${ports.mailpit})`);

  const testEnv = {
    ...process.env,
    E2E_WEB_PORT: ports.web,
    E2E_API_URL: apiUrl,
    E2E_MAILPIT_URL: `http://localhost:${ports.mailpit}`,
  };

  if (serve) {
    log("--serve: stack keeps running; in another terminal: cd frontend/e2e && npx playwright test   (Ctrl+C here stops everything)");
    await new Promise(() => {});
  }

  const tests = spawnSync(process.execPath, [join(here, "node_modules/@playwright/test/cli.js"), "test", ...playwrightArgs], {
    cwd: here,
    stdio: "inherit",
    env: testEnv,
  });
  log(`done in ${Math.round((Date.now() - started) / 1000)}s`);
  return tests.status ?? 1;
}

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    stopAll();
    infraDown();
    process.exit(130);
  });
}

let code = 1;
try {
  code = await main();
} catch (error) {
  console.error(`[e2e] ${error instanceof Error ? error.message : error}`);
} finally {
  stopAll();
  infraDown();
}
process.exit(code);
