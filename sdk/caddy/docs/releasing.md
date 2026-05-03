# Releasing caddy-stylobot

This document covers the full release process: one-time repo setup and the steps to cut each release.

---

## Repo setup (one time)

### 1. Create the standalone repo

Go to github.com and create a new public repository named `caddy-stylobot` under the `scottgal` account. Leave it completely empty (no README, no .gitignore, no license file). The sync workflow populates it from `sdk/caddy/` in the main stylobot repo.

### 2. Configure the deploy key

Generate an SSH keypair (no passphrase):

```bash
ssh-keygen -t ed25519 -C "caddy-stylobot deploy" -f caddy-stylobot-deploy -N ""
# Produces: caddy-stylobot-deploy (private) and caddy-stylobot-deploy.pub (public)
```

Add the public key to the standalone repo as a deploy key with write access:

- Go to `github.com/scottgal/caddy-stylobot` > Settings > Deploy keys > Add deploy key
- Title: `stylobot-monorepo-sync`
- Key: paste the contents of `caddy-stylobot-deploy.pub`
- Check "Allow write access"

Add the private key to the main stylobot repo as a secret:

- Go to the main stylobot repo > Settings > Secrets and variables > Actions > New repository secret
- Name: `CADDY_STYLOBOT_DEPLOY_KEY`
- Value: paste the full contents of `caddy-stylobot-deploy` (the private key)

Delete both key files from your local machine after adding them.

### 3. Verify the sync

Push any change to `sdk/caddy/` on the main branch. The `sync-caddy-module.yml` workflow should run and push the contents of `sdk/caddy/` to the `main` branch of `caddy-stylobot`. Check the Actions tab of the main repo to confirm it succeeds.

---

## Making a release

### 1. Confirm sync is current

Check that the sync workflow ran successfully for the latest commit that touched `sdk/caddy/` or `sdk/proto/detection.proto`. The `caddy-stylobot` main branch should reflect all recent changes.

### 2. Tag the release

From the `caddy-stylobot` repo (not the monorepo):

```bash
git clone git@github.com:scottgal/caddy-stylobot.git
cd caddy-stylobot
git tag v0.1.0
git push origin v0.1.0
```

Or tag directly on GitHub: go to Releases > Draft a new release > choose a tag > type `v0.1.0` > create tag on publish.

### 3. Wait for the release workflow

The `release.yml` workflow triggers on the `v*` tag push. It:

1. Builds Caddy binaries for 5 platforms (linux/amd64, linux/arm64, darwin/amd64, darwin/arm64, windows/amd64) using xcaddy
2. Creates a GitHub Release with all binaries and SHA256 checksums attached
3. Builds a multi-arch Docker image and pushes it to `ghcr.io/scottgal/caddy-stylobot` with both `latest` and the version tag

Monitor progress in the Actions tab of `caddy-stylobot`.

### 4. Verify the release

```bash
# Test xcaddy fetch from any machine
xcaddy build --with github.com/scottgal/caddy-stylobot@v0.1.0

# Test Docker pull
docker pull ghcr.io/scottgal/caddy-stylobot:v0.1.0
docker run --rm ghcr.io/scottgal/caddy-stylobot:v0.1.0 caddy list-modules | grep stylobot

# Verify a pre-built binary
curl -LO https://github.com/scottgal/caddy-stylobot/releases/download/v0.1.0/caddy-stylobot_linux_amd64
curl -LO https://github.com/scottgal/caddy-stylobot/releases/download/v0.1.0/caddy-stylobot_linux_amd64.sha256
sha256sum -c caddy-stylobot_linux_amd64.sha256
```

---

## Versioning

Use semantic versioning with a `v` prefix: `v0.1.0`, `v0.2.0`, `v1.0.0`.

During pre-1.0 (`v0.x.y`):

- Bump the **patch** version (`v0.1.0` to `v0.1.1`) for bug fixes, dependency updates, or documentation changes with no behavior change.
- Bump the **minor** version (`v0.1.0` to `v0.2.0`) for any change to Caddyfile directive names, header names, or the gRPC proto contract. These are breaking changes for users on `v0.x`.
- `v1.0.0` marks the stable API: the Caddyfile directives, header set, and gRPC contract are frozen. Reserve this for when the .NET sidecar is GA and the module has seen real production use.

---

## The Go module proxy

Once a tag is pushed to a public GitHub repository, the Go module proxy (`proxy.golang.org`) and checksum database (`sum.golang.org`) index it within a few minutes. No manual publishing step is needed.

Users can then fetch the module directly:

```bash
go get github.com/scottgal/caddy-stylobot@v0.1.0
```

And xcaddy resolves it when building:

```bash
xcaddy build --with github.com/scottgal/caddy-stylobot@v0.1.0
```

If the proxy has not indexed the tag yet (can happen in the first minute or two), run:

```bash
GOPROXY=direct go get github.com/scottgal/caddy-stylobot@v0.1.0
```

---

## Submitting to the Caddy module index

After `v1.0.0` is released and has seen real-world use, submit the module to the official Caddy module directory:

1. Check that `http.handlers.stylobot` is not already taken: browse to `caddyserver.com/docs/modules` and search for `stylobot`.
2. Follow the submission process at `caddyserver.com/docs/modules` (requires the module to be publicly available and the module ID to be registered via xcaddy's module registry).
3. The module ID is determined by the `CaddyModule()` function in `stylobot.go`. Confirm it returns `http.handlers.stylobot` before submitting.

Being listed in the Caddy module directory makes the module discoverable via `xcaddy build --with` autocomplete and the Caddy website module browser.
