# 0006 — Multi-target .NET 8 and .NET 10

**Status:** Accepted
**Date:** 2026-08-30

## Context

The library family (`LakeSpeak.Genie`, `LakeSpeak.Application`, `LakeSpeak.Configuration`,
`LakeSpeak.QuestionPacks`, `LakeSpeak.Rendering`) targets only `net10.0`. A consumer on
the LTS track wants `net8.0` support without forking the API. The CLI (`LakeSpeak.Cli`)
is a packaged `dotnet tool` that the MTP test infrastructure has a per-TFM shape for
(ADR 0005).

Two design choices were on the table:

1. **Lower the floor to .NET 8** by multi-targeting the entire repository. Each library
   publishes `lib/net8.0/` and `lib/net10.0/`. Tests run on both TFMs. The CLI keeps the
   single TFM the .NET 10 SDK's MTP mode prefers.
2. **Stay on .NET 10 only**, document that .NET 8 consumers must wait.

Path 1 is what was chosen. Per-TFM package versions use central package management's
`Update`+`Condition`; the `Microsoft.Extensions.*` packages follow the .NET runtime cadence
(net10.0 stays on the `10.x` servicing line, net8.0 drops to `8.0.x`). The test infrastructure differs by
TFM as documented in ADR 0005.

The one place the two TFMs diverge on resilience: the unsafe-method exclusion
for the standard resilience handler. `Microsoft.Extensions.Http.Resilience` 8.10.0
does not expose `HttpRetryStrategyOptions.DisableForUnsafeHttpMethods()`; that helper
landed in 9.x. The helper makes the standard resilience handler skip retries for
POST, PATCH, PUT, DELETE, and CONNECT — the Genie `start-conversation` and
`create-message` calls are POSTs that must not be retried, since a retry asks Genie
the same question again, runs the SQL warehouse a second time, and leaves an
orphaned conversation whose id the client never returns.

The net9.0+ line keeps the official `DisableForUnsafeHttpMethods()` call. The net8
line mirrors the helper at the `ShouldHandle` predicate: the public
`HttpClientResiliencePredicates.IsTransient` check first, then the unsafe-method
exclusion against the request method. On a transient exception the outcome's `Result`
is null, so the request has to come from `args.Context` — that fallback is what the
official helper does too (the standard resilience handler sets the request on the
context before the pipeline runs), and the net8 mirror has to do the same. Without
the context fallback, a socket reset on `start-conversation` would still be retried
on net8 and the contract would be broken on the exception path even though it holds
on the response path. The mirror uses the
`HttpResilienceContextExtensions.GetRequestMessage(ResilienceContext)` extension
marked `[Experimental]` in the resilience package; the warning is suppressed at the
Genie project level with a scoped `NoWarn=EXTEXP0001` and a comment explaining why.

The contract test `A_failed_start_conversation_is_never_retried` covers the response
path (the stub returns 503). A sibling test on the exception path (transient
`HttpRequestException` on a closed port) is added in the same change: it points
the client at a closed port, counts attempts through the resilience pipeline via
a counter `DelegatingHandler` added by an `IHttpMessageHandlerBuilderFilter`, and
asserts the count is 1.

## Decision

Multi-target the library family at `net8.0;net10.0` and keep the CLI at `net10.0`-only.
The unsafe-method exclusion (POST/PATCH/PUT/DELETE/CONNECT) is installed at the
`ShouldHandle` predicate on both TFMs, with the same context-fallback shape on both.
The mechanical parts:

1. `Directory.Build.props` — `<TargetFramework>net10.0</TargetFramework>` becomes
   `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`.
2. `Directory.Packages.props` — net8-line overrides for `Microsoft.Extensions.*`
   (8.0.x), `Microsoft.Extensions.Http.Resilience` (8.10.0), `Microsoft.NET.Test.Sdk`
   (17.14.1), `coverlet.collector` (6.0.4), and `Microsoft.Extensions.TimeProvider.Testing`
   (8.10.0). The default versions above remain the net10.0 values.
3. `tests/Directory.Build.props` — split the MTP shape: `UseMicrosoftTestingPlatformRunner`
   for net10, `TestingPlatformDotnetTestSupport` for net8. The `xunit.v3.mtp-v2` /
   `xunit.v3` package reference is conditioned on the TFM.
4. `src/LakeSpeak.Cli/LakeSpeak.Cli.csproj` — explicit `<TargetFrameworks>net10.0</TargetFrameworks>`,
   overriding the root. `tests/LakeSpeak.Cli.Tests` takes the same explicit `net10.0`
   target.
5. CI matrix — `tfm: [net8.0, net10.0]` on the build and test jobs. The net8.0 test
   cell uses a `tests/libraries-only.slnx` that excludes the net10.0-only CLI projects,
   so the slnx-level runner can proceed with `-f net8.0`. The net10.0 cell runs the
   full slnx-level test, which includes the CLI tests. The build job is also split
   per TFM so each TFM compiles once, in parallel.
6. `src/LakeSpeak.Genie/ServiceCollectionExtensions.cs` — `DisableForUnsafeHttpMethods()`
   on net9.0+; a hand-rolled `ShouldHandle` predicate on net8 that mirrors the helper
   and uses the same `ResilienceContext` fallback for the exception path. See
   Context above.

## Consequences

**Positive.** Library consumers on the LTS track (`net8.0`) can take a
`<PackageReference Include="LakeSpeak.Genie" />` without target-redirecting their app. The
package ships `lib/net8.0/` and `lib/net10.0/`. The CI matrix exercises both TFMs, so a
TFM-specific regression cannot merge silently. The unsafe-method exclusion behaviour is
identical on both TFMs, with a regression test for each branch (response and exception
path) of the contract.

**Negative.** The lock file now has a per-TFM entry for every multi-targeted project,
which makes the lockfile diff in this PR large. Expected.

**Local dev cost.** Two .NET SDKs on the build machine. The `global.json` pin selects the
.NET 10 SDK for MSBuild; `setup-dotnet` in CI installs the .NET 8 SDK additionally for
the net8.0 cell. The dual install costs ~30 seconds in CI per net8.0 cell.

**Not in this PR.** Lowering the floor to .NET 6 or 7 — explicitly out of scope.
Multi-targeting the CLI — out of scope by the tradeoff documented above.
