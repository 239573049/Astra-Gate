---
name: usage-writer-ceiling
description: Request logging performance, ordinal binding gains, and the sustained-load tradeoff behind a 2,000-record buffer
type: project
updated: 2026-10-20
source: session
evidence: ["src/Astra.Gateway/Pipeline/UsageWriter.cs:UsageWriter.QueueCapacity", "src/Astra.Data/RowInserter.cs:RowInserter", "src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.InsertBatchAsync"]
---
Measured on 2026-10-19 (AOT build, Apple M5, mock upstream at 0 ms, 64 concurrent requests for 10 s, every row logged):

| Build | Sustained rate | Drain afterwards | Peak memory |
|---|---|---|---|
| Before | 5.4k/s | 11 s | 521 MB |
| Read cache, unbounded queue | 14.5k/s (writer ~5.9k/s) | 14.6 s | 1.1 GB |
| + bounded queue (`QueueCapacity` 10 000) | 6.9k/s | 2.3 s | 262 MB |
| + `RowInserter` ("@"-named parameters) | 10.1k/s | 1.8 s | 253 MB |

Each request writes about 11 rows: the request (60 columns), ~8 trace spans and ~2 usage items. Microsoft.Data.Sqlite
binds every parameter by name on every execution. Dapper.AOT names them without a prefix (`id`), so each was looked
up four times (`id`, `@id`, `$id`, `:id`, each a linear scan): 68% of the writer's time. `RowInserter` creates
`@`-prefixed parameters once per batch for those three inserts. The counters stay on Dapper (a few rows per batch).

**Why bounded:** once the gateway out-ran the writer, the unbounded queue grew about 10 KB per request without limit,
so a long overload would end in an out-of-memory crash. With the bound, a finishing request waits in
`EnqueueAsync`, and no row is dropped. Real traffic (LLM calls lasting seconds) is about 100× below the ceiling.

**Originally deferred:** binding through raw SQLitePCL by index (the binder needed explicit storage-type and transaction tests).
The follow-up below measured and validated that approach. **Still rejected:** `PRAGMA synchronous=NORMAL` (a durability change, fsync was ~5%); storing spans as one JSON blob (schema and read-side
change, linked update 15).
**How to apply:** a new hot multi-row insert goes through `RowInserter`; if the ceiling ever matters again, profile
the writer first — the span count per request multiplies everything. Related: [[gateway-read-cache]],
[[dapper-aot-sequence-cast]].

**Independent review, 2026-10-19:** the ~10k/s result above is the full gateway workload's result, not a universal
SQLite or .NET limit. An isolated .NET 11 rc.2 osx-arm64 Native AOT run of the current `InsertBatchAsync` used fresh
migrated databases, real provider/token counters, 10,000 synthetic requests per case, two usage items and eight short
spans per request. Three rounds (alternating case order) verified request counts, span counts and token totals. Median
rates: batch 50 = 24,886/s; batch 200 = 27,989/s (+12.5%); batch 500 = 28,509/s (+14.6%); batch 50 with no spans =
46,220/s (+85.7%). Managed allocations inside persistence were 21,237 bytes/request with eight spans versus 10,565
without. This excludes HTTP, billing, span creation, the ledger and concurrent gateway CPU work; it is a cost-isolation
experiment, not an end-to-end improvement or a proposal to silently drop traces. Harness/results were kept under
`/private/tmp/astra-perf-review/` (`Program.cs`, `writer-aot-results.txt`).

The comparison's persistence contracts matter: Magpie at `f73378365f8764a891526aacc5b8dc6b9b0bf3c2` appends one JSONL
usage record (`internal/usage/usage.go:Append`, no explicit fsync there). Its live routing ring retains the most recent
60 routes (`internal/gateway/trace.go:traceKeep`), **and the CLI also persists completed routes asynchronously** in
`routing/YYYY-MM-DD.jsonl` (`trace.update` → `go saveRoute`, `internal/gateway/history.go:saveRoute`; `keepRoutes` defaults
to true). The earlier ring-only comparison was incomplete: the 2026-10-19 cross-product run verified 1,331,541 persisted
routes alongside the usage records. Astra persists each span and billing item with the request and counters. Compare
both actual persistence paths before attributing a throughput gap to Go versus .NET.

The channel bounds its buffered records, not all pending work: producers waiting in `WriteAsync` still hold their
records (also in the token ledger). Under an unbounded number of concurrent callers, this alone is not a hard memory
limit. A load test must measure the backlog/drain as well as returned HTTP responses; `RequestRecord.TotalMs` is set
before billing, trace materialization and `EnqueueAsync`, so that field does not include those costs.


**Implemented follow-up, 2026-10-19:** a narrow SQLitePCL ordinal binder replaced the named-parameter command for
requests, spans and usage items only. It retains one statement per table/batch on the caller's transaction; supports
only null/string/int/long/bool/double; checks cancellation per row and every SQLite result; disposes on all paths.
Round-trip tests cover embedded NUL, Unicode, nullable resets, REAL/INTEGER/TEXT storage classes and integers above
2^53. A late span constraint failure rolls back earlier requests and leaves the connection reusable. The existing
full suite and the shipped Native AOT plugin selftest passed. No schema, durability, trace-count or batching change.

Fresh isolated AOT medians (same 10,000-row cases as above, 3 rounds per variant): batch 50 / 8 spans, 23,960 →
41,394 requests/s (+72.8%); managed allocations 21,237 → 1,665 bytes/request (-92.2%); CPU 38.9 → 22.5 µs/request.
These are writer-only measurements, excluding row construction and the gateway. Larger batches remain a separate
experiment; the production batch size stays 50. Report and raw data: `out/performance/2026-10-19/` (local artifact).


The same follow-up compared complete gateway AOT binaries in 7 loopback HTTP scenarios × 3 alternating-order
rounds × 2 variants (8 seconds after warmup, fresh ASTRA_HOME each run). Registered 77-byte Chat requests at C64
measured 7,580 → 9,636 req/s (median, +27.1%); an extra 30-second C64 pair measured 6,323 → 7,012 (+10.9%).
Background CPU activity produced substantial cross-round variation, so these are workload-specific measurements,
not capacity guarantees. Peak RSS did not fall, single-concurrency P95 did not improve, and saturated runs still
held ~10k pending records. The 42 main runs checked 3,283,408 persisted requests including warmup, exactly 8 spans
and 2 usage items each, token counters and per-request cost; no HTTP errors or missing rows. This is why writer-only
+73% must not be advertised as gateway-wide +73%, nor allocation -92% as RSS -92%. The report preserves ranges,
paired deltas and drain time. That initial report had no fresh Magpie run; the same-day supplement now contains
30 matched short runs, local-auth checks and sustained-load diagnostics (`out/performance/2026-10-19/astra-vs-magpie-report.html`).
For Magpie the harness timed usage-log drain, then separately verified routing-history completeness after shutdown;
it did not time the route-history tail. Under macOS loopback HTTP/1.1, three C64 sustained runs encountered
upstream ephemeral-port allocation errors, including with a fresh upstream port. A final run observed 13,842
TIME_WAIT sockets; `vendorTransport.MaxIdleConnsPerHost = 8` is a candidate contributor, not a proven cause.
Do not generalize this to HTTP/2 or real upstream latency without another controlled test.

**Memory follow-up, 2026-10-20:** an initial capacity-only AOT experiment (small registered Chat request, C16,
two 8-second rounds, same batch size and persistence) measured peak RSS 245–265 MiB with 10,000 buffered records,
131–134 MiB with 1,000 and 145–151 MiB with 2,000. This initially favored a 1,000-record candidate; it is not an
object-by-object heap attribution. The 10,000-record version ends saturated runs with roughly 9,000 more unfinished
records, which can inflate short-window HTTP throughput by roughly 9,000 / measurement-seconds. Compare sustained
throughput and drain as well as HTTP completion. Pilot results: `out/performance/2026-10-20-memory/pilot-results.jsonl`.

The independent three-round comparison (45 runs: two Astra builds and Magpie, three saturated scenarios plus
1,000/5,000 req/s paced C16) confirmed median RSS 245.3 → 132.3 MiB at C16, 255.0 → 139.2 MiB at C64 and
249.5 → 134.3 MiB for SSE C16. HTTP throughput medians changed -6.5%, +0.3%, -5.2%; including usage drain, the
window completion rates changed +1.1%, +6.5%, -0.1%. These are noisy loopback measurements, not a throughput
guarantee. At fixed 1,000/5,000 req/s neither Astra build accumulated records and memory stayed roughly unchanged
(~100/~106 MiB); the capacity change targets saturation, not the application's base footprint. The batch size,
flush interval, records/spans/items and durability remain unchanged. Those measurements used the 1,000-record
candidate, not the final capacity selected below.

A single subsequent 30-second C64 run showed a ~30% throughput decrease with 1,000 records, so the choice was
rechecked with three rotating-order 30-second rounds per capacity (fresh processes/state/ports; all records verified).
Medians: 10,000 → 1,000 → 2,000 buffered records gave RSS 258.0 → 137.9 → 157.4 MiB and HTTP throughput
9,610 → 7,994 → 8,980 req/s; including usage drain, 9,195 → 7,753 → 8,697/s. **Final choice: 2,000**, retaining
~39% lower saturated RSS with a ~6.6% HTTP / ~5.4% drain-inclusive throughput cost in this experiment, instead of
the 1,000-record candidate's ~17% / ~16% cost. Do not describe this as a throughput win or discard the slower
single run. The application/background load varies substantially; low-load memory is largely unchanged.
Evidence: `out/performance/2026-10-20-memory/sustained-capacity-results.jsonl` and the report in that directory.

The final 2,000-record version was then compared afresh with the 10,000-record baseline and Magpie (27 runs,
C16/C64 saturation and paced 5,000 req/s, three rounds). Saturated RSS medians: C16 241.6 → 145.8 MiB; C64
261.2 → 149.7 MiB. Short-window HTTP throughput changed -1.4% / +0.1%; keep the separate sustained cost above
visible. Fixed 5,000 req/s RSS stayed ~105–106 MiB. Raw results: `final-main-results.jsonl` in the report directory.
