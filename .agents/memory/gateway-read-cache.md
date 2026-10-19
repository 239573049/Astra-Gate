---
name: gateway-read-cache
description: Why the gateway's per-request SQLite reads are cached the way they are (RowCache + per-file Generation + 2 s MaxAge), and what the 2026-10-19 profile found
type: project
updated: 2026-10-19
source: session
evidence: ["src/Astra.Data/RowCache.cs:RowCache", "src/Astra.Data/SqliteConnectionFactory.cs:SqliteConnectionFactory.Generation", "src/Astra.Data/SqliteSetup.cs:SqliteSetup", "src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.InsertBatchAsync", "tests/Astra.Data.Tests/RowCacheTests.cs"]
---
A 2026-10-19 benchmark against Magpie (mock upstream, macOS `sample` on the AOT binary) found that a small gateway
request spent most of its time in SQLite, not in Astra code. Three causes, fixed together:

- **A global mutex.** e_sqlite3 ships with memory statistics on, so every `sqlite3_malloc`/free took one process-wide
  mutex. Statement preparation allocates heavily, and 16 concurrent requests spent most samples in
  `__psynch_mutexwait` under `sqlite3Prepare`. `SqliteSetup` turns `SQLITE_CONFIG_MEMSTATUS` off before the first open.
- **PRAGMAs on every open.** Each query opened a pooled connection and ran three PRAGMAs first, so 4 statements were
  prepared per query. They now run once per physical connection (remembered by its SQLite handle).
- **~7 queries per request** (token, provider twice, provider model, system model, price table, …), all re-read
  rows that change only on admin writes. They are now served by `RowCache`.

Small-request throughput went from ~5.2k to ~9.2k rps at 16 concurrent after the first two fixes alone (opt1).

**Why this cache design:**
- Invalidation is per connection, not per method: a `TrackedConnection` compares `sqlite3_total_changes` at open and
  close and advances the file's `Generation` after commit. Per-method invalidation calls were rejected as easy to
  miss in ~30 write methods. SQLite's update/commit hooks were rejected too: they fire before commit, and native
  callbacks are a risk under AOT.
- Entries carry the generation read *before* the query, so a write that commits during a read can never be served.
- The counter is shared by every factory on the same file in the process, because `restore-all` builds a second
  `AstraDatabase` over the file a running host serves. The `Offline_Restore_All…` integration test caught this.
- `MaxAge` is 2 s because a separate `astra-server restore-all` process (CLI `astra restore-all` / `uninstall`) is
  invisible to the counter.
- Rows are cached, not entities: `Provider.Settings` is a `JsonObject`, which materializes lazily and is not safe to
  read from two threads.
- The request log writes through `OpenUntrackedAsync`. It commits many times a second and would otherwise flush the
  cache constantly; its only cached column is `tokens.last_used_at`.

**How to apply:** New writes to tokens / providers / provider_models / models / model_prices / clients /
client_bindings go through `Open`/`OpenAsync` (automatic for repository code). A new per-request read should use a
`RowCache` over its row type. Re-measure with a mock upstream before assuming a gateway slowdown is network.
Related: [[dapper-aot-sequence-cast]].

**Follow-up measurement, 2026-10-19:** an isolated .NET 11 rc.2 osx-arm64 Native AOT run of `ModelIdMatcher.Match`
over the seed's 168 models (10,000 calls per case, three rounds) measured a median 22.38 µs and 6,544 allocated bytes
for an unknown `bench-unlisted-model`; an exact match at the end of the list took 0.505 µs and 96 bytes. This excludes
database access. `EffectiveModelResolver` takes this fallback when the model has no provider-model row, and null row
results are deliberately uncached. A benchmark must state whether its model is registered: an unknown synthetic
model exercises materially different work from an ordinary configured model. Any future negative-result cache must
be bounded and respect write invalidation, and a precomputed matcher must preserve the existing match priority and
tie order. Harness/results: `/private/tmp/astra-perf-review/Program.cs` and `model-aot-results.txt`.


**Implemented follow-up, 2026-10-19:** the matcher now has an immutable precomputed index, sharing the candidates'
cache entry (not a second TTL layered over it); the provider upstream-ID dictionary is also immutable and cached.
This preserves exact → upstream-ID → alias → normalized priority and first-candidate tie order, verified against
the entire seed plus normalization variants. Fresh AOT unknown-model match: 23.220 → 0.097 µs/call,
6,544 → 0 managed bytes/call (steady state, index construction excluded). Provider-model misses opt into the same
4,096-entry, generation-aware, 2-second cache; tracked inserts invalidate immediately and external inserts expire.
Token-prefix misses still do not cache. The gateway resolves identity/capabilities first and merges billing once
after capability discovery; it does not reuse a pre-discovery billing snapshot across a network await.
