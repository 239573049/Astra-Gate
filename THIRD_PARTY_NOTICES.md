# Third-party notices

Astra bundles or vendors the following third-party code for its plugin system (`src/Astra.Plugins`).
NuGet and npm dependencies used elsewhere are listed by their own package metadata.

| Component | Where | License |
| --- | --- | --- |
| magpie `host.js` / `pi.js` ([yetone/magpie](https://github.com/yetone/magpie)) | vendored unmodified in `src/Astra.Plugins/Host/` (commit and hashes in `VENDORED.md`), embedded in `astra-server` | MIT — full text in `src/Astra.Plugins/Host/LICENSE-magpie.txt` |
| magpie plugin fixtures (`fake`, `word-guard`, `agent-aider`) | `tests/Astra.Plugins.Tests/Fixtures/magpie/` (tests only) | MIT — same license file beside them |
| [Jint](https://github.com/sebastienros/jint) | NuGet, linked into `astra-server` | BSD-2-Clause |
| [Acornima](https://github.com/adams85/acornima) (Jint's parser) | NuGet, linked into `astra-server` | BSD-3-Clause |
| [Bun](https://bun.sh) | not shipped: downloaded on demand (pinned version, SHA-256 checked) into `~/.astra/plugins/runtime/` | MIT (Bun's own notices ship in its release archive) |
