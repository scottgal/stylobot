# StyloBot Express sample — live demo at x.stylo.bot (or whichever subdomain).
# Self-contained: builds the @stylobot/core + @stylobot/node packages from
# sibling sources and runs the sample on $PORT.
#
# Build context: sdk/node (the workspaces root) so we get core + node + the
# sample in one image.
#   docker build -f sdk/node/samples/express-sample/Dockerfile -t stylobot-sample-express sdk/node
#
# Run:
#   docker run -e PORT=3001 -e STYLOBOT_URL=http://gateway:5080 \
#              -e STYLOBOT_API_KEY=... -p 3001:3001 stylobot-sample-express
#
# Behind a reverse proxy (Caddy / Cloudflare tunnel) on x.stylo.bot point
# the upstream at this container's PORT.

FROM node:22-alpine AS build
WORKDIR /sdk

# Copy the workspaces root + every workspace package + the sample. Order
# minimises layer churn: root files first so dep installs cache.
COPY package.json package-lock.json tsconfig.json ts-loader.mjs ./
COPY packages/core/package.json   packages/core/tsconfig.json   packages/core/
COPY packages/node/package.json   packages/node/tsconfig.json   packages/node/
COPY packages/elements/package.json packages/elements/tsconfig.json packages/elements/
COPY samples/express-sample/package.json samples/express-sample/tsconfig.json samples/express-sample/
RUN npm ci --workspaces --include-workspace-root

# Now the sources + build
COPY packages/core/src   packages/core/src
COPY packages/core/proto packages/core/proto
COPY packages/node/src   packages/node/src
COPY packages/elements/src packages/elements/src
COPY samples/express-sample/src samples/express-sample/src
COPY samples/express-sample/public samples/express-sample/public
RUN npm run build --workspaces --if-present

# --- runtime ----------------------------------------------------------------
FROM node:22-alpine AS runtime
WORKDIR /app

# Bring across only the built workspaces + the sample's runtime files. node
# resolves the @stylobot/* names against /app/node_modules where they're
# symlinked to the workspace dirs.
COPY --from=build /sdk /app

ENV NODE_ENV=production \
    PORT=3001 \
    STYLOBOT_URL=http://localhost:5080

EXPOSE 3001

# tini-style PID 1 is fine via node directly for a hot reload-free server.
WORKDIR /app/samples/express-sample
CMD ["node", "--experimental-strip-types", "src/server.ts"]
