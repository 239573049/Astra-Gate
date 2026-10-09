# Astra

English | [简体中文](README.zh-CN.md)

[![LINUX DO · 新的理想型社区](https://cdn3.ldstatic.com/original/4X/d/6/5/d65def8cc0c413f318bee2bcd1c774bc4ad109a8.png)](https://linux.do/)

**Astra** is a local AI gateway that runs on your own machine. It sits between your AI
clients (Codex, Claude Code, Gemini CLI, …) and AI providers (Anthropic, OpenAI, DeepSeek,
OpenRouter, …) as a local relay:

```
Codex ────────┐                                     ┌─ Anthropic
Claude Code ──┤   Astra gateway (127.0.0.1:17321)   ├─ OpenAI
Gemini CLI ───┤   protocol conversion · billing ·   ├─ DeepSeek
OpenCode ─────┘   privacy guardrails                └─ OpenRouter / custom…
```

## Features

- **One-click client take-over** — no hand-editing of `~/.codex/config.toml`,
  `~/.claude/settings.json` and friends. Enable a client in the web console (or via the
  CLI) and Astra rewrites its config with an automatic backup of the original; disable it
  any time to restore exactly what was there before.
- **Automatic protocol conversion** — clients may speak OpenAI Chat, OpenAI Responses,
  Anthropic Messages or Gemini. Astra decodes each request into a common intermediate
  representation and re-encodes it for the upstream, so client and provider never have to
  speak the same protocol.
- **Visible usage and cost** — input / output / cache / reasoning tokens and cost are
  recorded per request in a local SQLite database, with a dashboard and a per-request
  billing breakdown.
- **Privacy guardrails** — requests are scanned locally for API keys, email addresses,
  internal hostnames and other sensitive data before they leave the machine. Guardrails can
  warn, block, or redact (placeholder out, original restored on the way back; the original
  values never leave your machine).
- **Multi-account** — add several provider entries and subscription accounts for the same
  provider and switch a client between them without touching its config again.
- **Local-first** — API keys are encrypted at rest with DataProtection in `~/.astra/keys`;
  request bodies and billing data live in local SQLite (`~/.astra/astra.db`). No cloud
  account required.

## Install

Requires Node.js ≥ 18 (npm installs a platform-specific server binary; no .NET needed).

```bash
npm install -g @aidotnet/astra-gate   # the `astragate` package name is identical
```

Prefer a GUI? Install the desktop app (Electron: console, tray icon, service lifecycle)
in one command:

```bash
npx @aidotnet/astra-gate install --desktop
```

Standalone installers — macOS `.dmg`, Windows `.exe`, Linux `.AppImage` with the server
bundled (no Node.js required) — are on the [download page](https://astra-gate.si/download).

Supported platforms: macOS, Windows and Linux, each on x64 and arm64.

## Quick start

```bash
astra start        # start in the background, listens on 127.0.0.1:17321
astra status       # confirm it is running
astra open         # open the web console in your browser
```

If port 17321 is taken, Astra picks a free port and records the real one in
`~/.astra/runtime.json` — the CLI and console always read it from there, so don't hard-code
the port in scripts.

Add a provider in the console (**Providers** → **Add provider** → pick a template, paste an
API key, **Test connection**). Then point anything that speaks a supported protocol at the
gateway:

```bash
# OpenAI-compatible
curl http://127.0.0.1:17321/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"Hello, Astra"}]}'

# Anthropic-compatible
curl http://127.0.0.1:17321/anthropic/v1/messages \
  -H "Content-Type: application/json" \
  -d '{"model":"claude-sonnet-4-5","max_tokens":1024,"messages":[{"role":"user","content":"Hello, Astra"}]}'

# Gemini-compatible
curl "http://127.0.0.1:17321/gemini/v1beta/models/gemini-2.5-pro:generateContent" \
  -H "Content-Type: application/json" \
  -d '{"contents":[{"parts":[{"text":"Hello, Astra"}]}]}'
```

Any model name that exists in the model catalog (or was added under a provider) is routed
to a matching upstream, converted across protocols when needed. See
[docs/guide/first-request.mdx](docs/content/docs/guide/first-request.mdx) for the full
walkthrough.

Finally, instead of pointing clients at the gateway by hand, let Astra manage them —
**Clients** page → **Enable**, or:

```bash
astra client enable codex --provider deepseek --model deepseek-chat
```

## Supported clients

`codex` · `claude-code` · `gemini-cli` · `opencode` · `claude-desktop` · `grok-build` ·
`pi` · `hermes-agent` · `minimax-code` · `copilot-cli` · `vscode-copilot` · `crush` · `qwen-code` ·
`droid` · `kimi-code` · `zed` · `vscode-insiders` · `vscodium` · `omp` · `mimo-code` ·
`deepseek-harness`

Zed never reads an API key from its settings file, so paste an Astra token into Zed's
provider settings once after enabling.

Take-over backs up the original file before first write, keeps permanent first-write
backups plus 20 rolling ones, and never restores a value you changed yourself afterwards.
`astra restore-all` (or uninstalling) puts every managed config back offline, without the
server running.

## CLI

`astra` with no subcommand behaves like `astra start`.

| Command | Purpose |
| --- | --- |
| `astra start` | Start the server (background by default; `--port`, `--host`, `--foreground`, `--open`) |
| `astra stop` / `restart` / `status` | Manage the running server |
| `astra logs [-f]` | Show recent server logs |
| `astra open` | Open the web console |
| `astra autostart enable\|disable\|status` | Start at login (LaunchAgent / systemd user / schtasks) |
| `astra provider list\|add\|remove` | Manage upstream providers |
| `astra client list\|status\|enable\|disable` | Manage client take-over |
| `astra config set-password` | Set the admin password (required before binding a non-loopback address) |
| `astra update [--check]` | Update Astra (and the desktop app), then restart the server |
| `astra restore-all [--purge]` | Restore every managed client config, offline |
| `astra uninstall [--desktop] [--purge]` | Uninstall Astra (restores client configs, stops the server) |

## Security model

- The server listens on `127.0.0.1` by default. Binding a non-loopback address is refused
  until an admin password is set (`astra config set-password`).
- Every mutating admin API call requires an `X-Astra-Admin: 1` header; the Host header is
  validated against a loopback allowlist as a DNS-rebinding defense.
- Provider API keys are encrypted with DataProtection; the API and console only ever show
  masks. Secrets and request bodies are never logged.

## Where data lives

Everything is under `~/.astra/` (override with the `ASTRA_HOME` environment variable):

| Path | Contents |
| --- | --- |
| `config.json` | Startup options (read before the server starts) |
| `runtime.json` | The pid / port / API version actually in use |
| `astra.db` | SQLite (WAL): providers, models, request logs, usage and billing |
| `keys/` | DataProtection-encrypted provider API keys |
| `update-state.json` | State for the staged download → verify → swap → restart update flow |

## Desktop app

The desktop app (`npx @aidotnet/astra-gate install --desktop`, or the standalone
installers) wraps the same console in an Electron shell: a tray / menu-bar icon with service
status, start / stop / restart, per-client provider switching, update checks, and the option
to launch at login staying in the tray.

Server updates use a staged flow — download → sha256 verify → swap → restart, with
automatic rollback on failure — and never replace a running binary in place.

## Development

Prerequisites: [.NET SDK](https://dotnet.microsoft.com/) 10.x (pinned by `global.json`),
Node.js ≥ 18 and [pnpm](https://pnpm.io) 9.15.0 (`corepack enable` works).

```bash
pnpm install                        # install Node workspace dependencies

# .NET side (solution Astra.sln)
dotnet build -warnaserror           # warnings are errors
dotnet test --no-build              # xunit, one test project per src project
dotnet run --project src/Astra.Server -- serve   # run the server directly

# Node side
pnpm -r --if-present lint           # eslint
pnpm -r --if-present typecheck      # tsc --noEmit
pnpm -r --if-present test           # vitest
pnpm -r --if-present build          # cli, web, docs, desktop, packages

# iterate on a single package, e.g.
pnpm --filter @aidotnet/web dev     # admin UI on http://localhost:5173
pnpm --filter astragate test        # CLI tests
pnpm --filter @aidotnet/docs dev    # documentation site
```

### Repository layout

| Path | What it is |
| --- | --- |
| `src/Astra.Core` | Domain logic: billing, pricing, models, privacy (no NuGet dependencies) |
| `src/Astra.Data` | SQLite persistence (Dapper, WAL) and migrations |
| `src/Astra.Clients` | Client config adapters and take-over/restore |
| `src/Astra.Providers` | Provider templates |
| `src/Astra.Gateway` | Gateway pipeline and the four protocol codecs |
| `src/Astra.Server` | ASP.NET Core host (`astra-server`), admin API, security middleware |
| `cli/` | The `astragate` npm package (`astra` command) and its platform server packages |
| `web/` | Bilingual (zh/en) admin console, Vite + React |
| `desktop/` | Electron app |
| `docs/` | Fumadocs documentation site and official website |
| `npm/` | Platform packages `@aidotnet/server-*` / `@aidotnet/desktop-*` |
| `packages/update-core` | Shared update engine (CLI + desktop) |
| `tests/` | xunit test projects, one per `src` project |

Protocol conversion is hub-and-spoke: each wire protocol decodes into a common IR
(`src/Astra.Gateway/Protocol/Ir.cs`) and re-encodes from it — never pairwise.

## Documentation

The docs live in [`docs/content/docs`](docs/content/docs) and are served by the site in
`docs/` (`pnpm --filter @aidotnet/docs dev`). Notable pages:
[installation](docs/content/docs/guide/install.mdx),
[first request](docs/content/docs/guide/first-request.mdx),
[console tour](docs/content/docs/console/index.mdx),
[CLI reference](docs/content/docs/cli/index.mdx) and
[privacy](docs/content/docs/advanced/privacy.mdx).

## License

MIT. Release notes: [CHANGELOG.md](CHANGELOG.md).
