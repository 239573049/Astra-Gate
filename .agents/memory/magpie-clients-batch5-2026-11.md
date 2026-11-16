---
name: magpie-clients-batch5-2026-11
description: Batch 5 of the magpie client adapters (desktop apps, chat apps, bots): six file-based adapters; nine agents left out because they only take a provider through a live API, socket, link or sealed store
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Clients/Adapters/QwenPawClientAdapter.cs", "src/Astra.Clients/Adapters/OpenMausBotClientAdapter.cs", "src/Astra.Clients/Adapters/AstrBotClientAdapter.cs"]
---
Added from magpie's wiring (checked against magpie's code only, no live app): liveagent, openmausbot, astrbot, qwenpaw, openclaw, openbot.

- **Not adapters (adapters plan edits of files Astra owns):** Alma (local REST API on :23001), Shelley (Unix socket API, models in its
  database), Lody (`lody://provider/import` confirmation dialog), Cindy (`cindy://provider/import`, keys encrypted in its database),
  AionUi (`aionui://add-provider`, database), Cherry Studio (`cherrystudio://providers/api-keys`, SQLite) take a provider only through a
  live API / socket / link. Avibe and Kun add a source through the running app's internal API because the key lives in a protected
  credential store under a reference (a file entry would carry no key). OpenHanako's `provider-catalog.json` is sealed since 1.0
  (`HANA-SECRET-1.AES-256-GCM`, key in the keychain), so it can be neither read nor written. A "Connect" link / API step in the console would
  be a new mechanism, not an adapter.
- **OpenBot is keyless** like fx: the entry's `secret` is Electron safeStorage ciphertext. The status card warns that "require gateway key" must be
  off for local requests (they count as the default token). A secret the user gives the entry inside OpenBot is kept on re-enable.
- **OpenMausBot owns the whole `instances` map when the file has none.** A saved map is the fleet, so a map holding only `astra` would drop every
  other engine: Astra then writes OpenMausBot's DEFAULT_FLEET (copied from magpie, commit 516ca75 of OpenMausBot) as one recorded value
  (`instances`), and a disable restores the original absence. Once Astra wrote the fleet, re-enables keep targeting `instances`, never
  `instances.astra` (a second recorded original would restore Astra's own instance). The fleet list will drift from OpenMausBot's real default.
- **QwenPaw** writes two files: `<secret>/providers/custom/astra.json` (per-key changes; QwenPaw's `ENC:` re-encryption of `api_key` is not drift, and an
  encrypted key stays until the key rotates) and the default agent's `active_model` in `agent.json` (only when that file exists and `backend`
  is `qwenpaw`). Other agents (`qwenpaw#<id>` rows in magpie) are not wired; a symlinked working folder is not resolved.
- **AstrBot** owns array elements by `[id=…]` selectors: the `provider_sources` element and one `provider` element per model (`astra/<model>`);
  stale model elements go out on a re-enable. Ids containing `[ ] = &` are skipped (selector syntax). The file's BOM is kept. Docker AstrBot is not wired.
- **OpenClaw** does not touch the pre-2026.8.1 `agents.defaults.models` allow-list (its keys contain dots, which the key-path syntax splits) nor
  per-agent models; `modelPolicy.allow` gets `astra/*` only when it is already a non-empty array.
- Disable of all six is "every recorded key of the client" (`Store.List`), the Goose pattern; drift is the applier's call.
- Icons are monograms; homepages for LiveAgent, OpenMausBot and OpenBot (openbot.run) are best guesses; install is Manual except OpenClaw (npm `openclaw`).
