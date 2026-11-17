---
name: docker-auto-update
description: Docker auto-update (user request 2026-11-17) = Astra decides, a label-scoped nickfedor/watchtower sidecar pulls; the server never gets the Docker socket; token shared through /data/updater-token
type: project
updated: 2026-11-17
source: session
evidence: ["src/Astra.Server/Hosting/ContainerUpdater.cs", "compose.yaml:updater", "tests/Astra.Server.IntegrationTests/UpdateApiTests.cs:Apply_asks_the_container_updater_and_records_the_request"]
---
On 2026-11-17 the user asked for automatic updates in the compose deployment, with the server taking part. Built: compose.yaml runs
`nickfedor/watchtower:1.24` (the maintained fork; containrrr/watchtower was archived 2025-12-17) with only the `update` HTTP endpoint,
no schedule (`WATCHTOWER_HTTP_API_PERIODIC_POLLS` unset), `WATCHTOWER_LABEL_ENABLE` and cleanup. Astra (`ASTRA_UPDATER_URL` +
token) asks it `POST /v1/update?async=true` from `POST /api/update/apply` or, while `AppSettings.UpdateAutoApply` (default on), from
`UpdateCheckWorker` after a check finds a version — at most hourly per version, because the image is pushed after the feed announces it.

**Why:** Astra keeps the channel, the setting and the UI; giving the gateway (reachable from the network) the Docker socket would hand
out root on the host. Rejected: the server pulling/recreating itself through the socket; an in-container binary swap (image tag and
binary would drift).

**How to apply:** the token is `ASTRA_UPDATER_TOKEN_FILE=/data/updater-token`, created by Astra on start and read by the updater from
the same volume mounted read-only — Watchtower reads a token *file* only if it exists when it starts (`isFilePath` in its
`internal/settings/flags.go`), else the path string becomes the token, hence `depends_on: service_healthy`. The image tag, not the
channel setting, decides what is pulled (`latest` stable, `preview`). Verified only with a fake updater on TestServer and
`docker compose config`; never run against a real Watchtower and registry.
