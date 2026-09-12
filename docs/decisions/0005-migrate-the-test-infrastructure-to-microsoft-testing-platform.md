# 0005 — Migrate the test infrastructure to Microsoft Testing Platform

**Status:** Accepted
**Date:** 2026-08-29

## Context

`Microsoft.NET.Test.Sdk` 18.9.0 — the first test-SDK version on a Dependabot major-version bump —
transitively pulls in `Microsoft.Testing.Platform` 2.3.3, and that combination breaks the
project's tests on .NET 10 SDK. The failure mode is:

    Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
    on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the
    new dotnet test experience. (https://aka.ms/dotnet-test-mtp-error)

Both `test (ubuntu-latest)` and `test (windows-latest)` fail with this on the bump PR
(dependabot/nuget/test-c0a258730e, closed 2026-08-29). The project is on .NET 10 SDK and the
VSTest support for that path is now legacy: the .NET docs at
https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test are explicit that
"running MTP projects under VSTest mode is considered legacy in favor of the newer experience
in .NET 10 SDK. The support of running under this mode will be removed in MTP version 2 if run
with .NET 10 SDK."

Two paths were on the table:

1. **Legacy-compatible** — add the `Microsoft.Testing.Platform.MSBuild` package to every test
   project and set `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` in
   `Directory.Build.props`. Keeps `dotnet test` in VSTest mode with an MTP bridge. Buys time.
2. **Modern MTP mode** — add `"test": { "runner": "Microsoft.Testing.Platform" }` to `global.json`.
   `dotnet test` switches to MTP mode.

Path 1 is a stopgap that needs a follow-up anyway (the MTP-2.0 transition will remove the
bridge). Path 2 is what the .NET 10 docs steer toward and what xunit v3 supports natively —
`xunit.v3` 3.1.0+ ships MTP support directly, and an explicit `xunit.v3.mtp-v2` package is also
available if a tighter pin is needed.

The project uses xunit v4 with its `xunit.v3` package identity and Microsoft Testing Platform;
the exact package versions remain centrally pinned in `Directory.Packages.props`, and the selected
xunit and Test SDK versions are MTP-compatible. Adopting MTP mode requires only the `global.json` flip and the
matching CLI changes — `--filter` becomes `--filter-trait`, `--logger trx` becomes
`--report-trx` — and no package additions.

## Decision

Move the test infrastructure to Microsoft Testing Platform mode of `dotnet test` by:

1. Adding `"test": { "runner": "Microsoft.Testing.Platform" }` to `global.json`. This is the
   one-line switch the .NET 10 SDK recognises; no per-project `TestingPlatformDotnetTestSupport`
   property is needed.
2. Updating the CI test commands from VSTest to MTP shape:
   - `dotnet test --filter "Category!=Live" --logger trx --results-directory TestResults` →
     `dotnet test --filter-not-trait "Category=Live" --report-trx --output TestResults --ignore-exit-code 8`
   - xunit v3's MTP filter is `--filter-not-trait "name=value"` (no `!=` syntax;
     `--filter-trait` matches positively), which differs from VSTest's `--filter`. The
     `--ignore-exit-code 8` suppresses MTP's "zero tests in a project" exit code, which
     fires for the `LiveIntegrationTests` project once the Live trait is filtered out.
   - Same change for `live-smoke.yml`: the `Category=Live` filter is now
     `--filter-trait "Category=Live"`, and the artifact upload path uses `--output TestResults`.
3. Leaving `xunit.v3` and `Microsoft.NET.Test.Sdk` in place. They are MTP-compatible. No package
   additions; the migration is the switch, not a tooling swap.
4. **Until this lands**, the test-group major-version Dependabot group is effectively broken:
   any future bump that pulls in MTP ≥ 2.0 will produce a red PR like #77. The right call is
   to merge this migration before the next test-group bump; after that, the major-version
   ignore in `.github/dependabot.yml` can be removed.

## Consequences

**Positive.** The test-group major-version Dependabot bump can land without producing a red
PR. The .NET 10 SDK's recommended test experience is in use, and the path is forward-compatible
with MTP ≥ 2.0 and with the future removal of the VSTest-mode-with-MTP-bridge shim.

**Negative.** The TRX output path changes from `--results-directory TestResults` to
`--output TestResults`. The artifact-upload step in `ci.yml` and `live-smoke.yml` already
points at `TestResults/`; the path under it changes from `*.trx` to `*.trx` (same file
extension, different layout). The artifact name `test-results-${{ matrix.os }}` does not need
to change.

**Local dev cost.** Anyone running `dotnet test` locally needs .NET 10 SDK 10.0.303 or later.
The project's `global.json` already pins 10.0.303 with `rollForward: latestPatch`, so this is the
same constraint that CI already enforces.

**Compatibility statement.** The MTP mode is supported by `xunit.v3` 3.1.0+ and
`Microsoft.NET.Test.Sdk` 18.9.0+. Both are present in the current `Directory.Packages.props`
pinned ranges. The migration is forward-only; rolling back to VSTest mode is possible by
reverting `global.json` and the workflow command changes, but no contributor has asked for
that.
