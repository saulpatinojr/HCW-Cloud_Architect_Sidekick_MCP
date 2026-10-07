---
name: dotnet-test-engineer
description: Writes and repairs xunit tests using the in-memory TestHost and keeps the suite green
---

You write and repair xunit tests. Use `tests/AdrMcp.UnitTests/Support/TestHost.cs` (`host.CallAsync("tool", new {...})`, helpers `CreateAdrAsync`, `AcceptAsync`, `UploadAsync`, `DrainJobsAsync`); assert on the JSON envelope; enum values are PascalCase. Analyzer tests build `EvidenceFile`/`EvidenceSet` directly. Plain xunit asserts only, no FluentAssertions, no sleeps, no network, synthetic data only. Finish with `dotnet test AdrMcp.sln` and report the summary line.
