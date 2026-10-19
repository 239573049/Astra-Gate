---
name: dapper-aot-sequence-cast
description: Dapper.AOT 1.1.0 throws InvalidCastException at run time for a List<anonymous> sequence parameter; pass IEnumerable<anonymous>
type: project
updated: 2026-10-19
source: session
evidence: ["src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.InsertBatchAsync", "tests/Astra.Data.Tests/RequestRepositoryTests.cs:A_Batch_Adds_Up_To_The_Same_Counters_As_Its_Records_One_By_One"]
---
`conn.ExecuteAsync(sql, rows.Select(r => new { ... }).ToList(), tx)` builds and is intercepted without a warning.
But the generated interceptor casts the argument to `List<object?>` (it cannot name the anonymous type), so every
call throws `InvalidCastException: Unable to cast List<<>f__AnonymousType…> to List<object>` at run time. This
was found on 2026-10-19 when `InsertBatchAsync` moved to one prepared command per statement.

**Why:** `List<T>` is invariant. With the static type `IEnumerable<anonymous>` (drop the `.ToList()`), the generator
casts to `IEnumerable<object?>`, which anonymous reference types satisfy through covariance.

**How to apply:** pass a sequence of anonymous rows as `IEnumerable<…>`, or use a named `internal` row type. To see
what Dapper.AOT generated, build with `--no-incremental -p:EmitCompilerGeneratedFiles=true
-p:CompilerGeneratedFilesOutputPath=<dir>` and read `Dapper.CodeAnalysis.DapperInterceptorGenerator/*.generated.cs`.
Tests exercise the real interceptors (the src assembly keeps them even though the test assembly opts out), so a
repository test catches it. Related: [[gateway-read-cache]].
