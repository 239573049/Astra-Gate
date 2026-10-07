# Astra docs site (fumadocs + Next.js) — the official website image.
#
# Build context is the REPOSITORY ROOT (the pnpm workspace root), because the
# frozen lockfile has importers for every workspace member:
#
#   docker build -f docs/Dockerfile -t astra-docs .
#
# Runtime: next standalone output (see docs/next.config.mjs `output`).
FROM node:24-alpine AS base
RUN corepack enable
WORKDIR /repo

# ---------- deps: only the docs dependency subtree ----------
FROM base AS deps
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
# Every lockfile importer needs its package.json present for --frozen-lockfile.
COPY cli/package.json cli/
COPY web/package.json web/
COPY desktop/package.json desktop/
COPY packages/update-core/package.json packages/update-core/
COPY npm/ npm/
COPY docs/package.json docs/
RUN pnpm install --frozen-lockfile --filter @aidotnet/docs

# ---------- build: next build with standalone output ----------
FROM base AS build
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY --from=deps /repo/node_modules ./node_modules
COPY --from=deps /repo/docs/node_modules ./docs/node_modules
COPY docs ./docs
RUN pnpm --filter @aidotnet/docs build

# ---------- runtime: standalone server only ----------
FROM node:24-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    PORT=3000 \
    HOSTNAME=0.0.0.0 \
    DATA_DIR=/app/data
# Client-release storage (upload API + download feed); mount a volume so
# releases survive image upgrades:  docker run -v /srv/astra:/app/data …
RUN mkdir -p /app/data
# Next detects the workspace root (/repo), so the standalone tree mirrors it:
# server.js lives at docs/server.js with hoisted node_modules beside it.
COPY --from=build /repo/docs/.next/standalone ./
COPY --from=build /repo/docs/.next/static ./docs/.next/static
COPY --from=build /repo/docs/public ./docs/public
EXPOSE 3000
VOLUME ["/app/data"]
CMD ["node", "docs/server.js"]
