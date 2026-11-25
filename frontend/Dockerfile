# syntax=docker/dockerfile:1.7
# Web image (apps/site: public pages, sign-in, /learn, /teach, /ops). Build context: frontend/.
# Build on a machine of the target architecture (CI: amd64 and arm64 runners): the standalone output ships
# native modules (sharp) for the architecture it was built on. A cross build (--platform) runs under QEMU.

FROM node:20-bookworm-slim AS base
ENV PNPM_HOME=/pnpm \
    NEXT_TELEMETRY_DISABLED=1
ENV PATH=$PNPM_HOME:$PATH
RUN corepack enable

FROM base AS build
WORKDIR /repo
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY apps/site/package.json apps/site/
COPY packages/i18n/package.json packages/i18n/
COPY packages/session/package.json packages/session/
COPY packages/ui/package.json packages/ui/
# Workspace member of the end-to-end tests: only its manifest, so the frozen lockfile matches (no browsers are installed).
COPY e2e/package.json e2e/
RUN --mount=type=cache,id=pnpm,target=/pnpm/store pnpm install --frozen-lockfile
COPY . .
# Standalone output: a minimal server.js with only the traced node_modules (see apps/site/next.config.ts).
ENV NEXT_STANDALONE=1
RUN pnpm --filter @konnyaku/site build

FROM node:20-bookworm-slim AS web
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0
WORKDIR /app
COPY --from=build --chown=node:node /repo/apps/site/.next/standalone ./
COPY --from=build --chown=node:node /repo/apps/site/.next/static ./apps/site/.next/static
# Graceful SIGTERM for rolling deploys (finish requests in flight instead of exiting at once).
COPY --chown=node:node graceful-shutdown.cjs ./
ENV NEXT_MANUAL_SIG_HANDLE=true
USER node
EXPOSE 3000
CMD ["node", "--require", "./graceful-shutdown.cjs", "apps/site/server.js"]
