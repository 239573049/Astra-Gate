---
name: astra-server-flag-first-serves
description: astra-server has no --help; any flag-first call (`--help`, `--port 1`) and `serve --help` run `serve` on ~/.astra and overwrite runtime.json
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Server/Program.cs:Program.Main (args[0] starting with '-' → \"serve\")", "src/Astra.Server/Hosting/RuntimeFile.cs:RuntimeFile.Write/Delete", "src/Astra.Server/Hosting/ServerOptions.cs (serve ignores flags it does not know)"]
---
`Program.Main` treats a first argument that starts with `-` as options for the default `serve`, so
`astra-server --help` does not print help: it starts a full second server on the real `~/.astra`.
On 2026-10-19, while setting up a benchmark, `/Applications/Astra.app/.../astra-server --help` started
beside the desktop's server, drifted to 17322 (`PortPicker`) and overwrote `runtime.json`. When it
exited it deleted that file (`RuntimeFile.Delete` removes the entry that names its own pid), so the
desktop's server kept running on 17321 with no `runtime.json` at all until its next restart.
It happened again on 2026-10-30 with `astra-server serve --help`: `serve` ignores flags it does not
know (`--help`, `--strict-port`), so naming the subcommand does not make it safe either.

**Why:** A second server shares the SQLite database and background workers with the running one, and
the lost `runtime.json` takes the runtime token (`/api/admin/shutdown`) with it. Related:
[[orphaned-server-after-failed-update]].
**How to apply:** Read the subcommand list in the `Program.cs` header instead of running the binary
to see its usage. To run a throwaway server, set `ASTRA_HOME` to a scratch directory and pass
`serve --port <free port>` explicitly. If `runtime.json` is missing while a server answers on the
configured port, a stray server is the likely cause; restarting the desktop app rewrites the file, or —
if you read it before the stray started — write the same JSON back (mode 600) so the token survives.
