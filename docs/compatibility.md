# Compatibility

A record of what was tested, against what, and when. Not a support matrix, and not a promise.

An entry appears here only once someone has actually run it. Paths that have not been exercised are
listed as untested rather than assumed to work, and identical API paths across clouds are **not**
evidence that a cloud works.

## Runtime

| LakeSpeak | .NET | Databricks CLI | Genie API | Status |
|---|---|---|---|---|
| 0.3.x | 8.0, 10.0 | 1.10.0 | `/api/2.0/genie`, GA | Library family multi-targets 8.0 and 10.0; the `lakespeak` CLI is 10.0-only. See [ADR 0006](decisions/0006-multi-target-net8-and-net10.md) |
| 0.1.x — 0.2.x | 10.0 | 1.10.0 | `/api/2.0/genie`, preview when released | Library family and CLI are 10.0-only |

The Genie Conversation API became [generally available on
2026-04-02](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026#april-2-2026). Visualization
retrieval became [generally available on
2026-08-26](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026#august-26-2026), but LakeSpeak
does not use that path and has not exercised it live.

## Platforms

| Platform | Status |
|---|---|
| Linux x64 | Unit and contract tests run in CI |
| Windows x64 | Unit and contract tests run in CI |
| macOS arm64 | Unit and contract tests run in CI (since the `macos-latest` matrix entry was added) |

## Clouds

| Cloud | Status |
|---|---|
| Azure Databricks | **Verified against a live workspace on 2026-08-01** — see below |
| AWS Databricks | **Verified against a live workspace on 2026-09-01** — see [Live verification, AWS](#live-verification-aws--through-2026-09-01) below. The PAT path, native OAuth M2M, scheduled live suite and multi-chunk composition were exercised. Closes [#51](https://github.com/ivanvyd/LakeSpeak.NET/issues/51) |
| GCP Databricks | Not tested — [#52](https://github.com/ivanvyd/LakeSpeak.NET/issues/52). Same shape as AWS. **The contribution that closes #52 is a PR that runs the live suite on a GCP workspace and records the result here.** |

## Live verification, 2026-08-01

Run against an Azure Databricks premium workspace in `eastus2`, on a serverless SQL warehouse,
against a Genie Agent over a synthetic revenue table created for the purpose. Authentication was an
Entra ID access token for resource `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, supplied through
`DATABRICKS_TOKEN`.

| Path | Result |
|---|---|
| `agents list` (table and json) | Listed the live Agent with its real id |
| `ask` with `--show-sql` | Real answer, real result table, real generated SQL, exit 0 |
| `ask --format json` | Valid UTF-8, versioned schema, on stdout only |
| `ask --format csv` | Clean CSV on stdout with diagnostics on stderr, verified via `2>/dev/null` |
| `ask` against an unknown Agent | Real listing lookup, exit 2 |
| `pack run` | Two questions, Markdown report written, exit 0. One of the two came back as a Genie clarifying question rather than an answer — see [limitations](limitations.md) |
| Decimal fidelity | `4500000.00`, `3350000.50`, `1780000.25` reached CSV, JSON and Markdown byte-identical to what Databricks returned |
| Column types | `DECIMAL(22,2)` — precision and scale preserved, which `type_name` alone would have lost |
| Non-ASCII | `€` intact in a file-written report |
| `export last` | Re-fetched the result from Databricks and wrote correct CSV |
| `feedback last` | Positive rating with a comment accepted |
| Opt-in live suite | 8 tests green, including follow-up conversations, feedback and cancellation |

The live suite in `tests/LakeSpeak.LiveIntegrationTests` reproduces all of this. Run it with
`DATABRICKS_HOST`, `DATABRICKS_TOKEN` and `LAKESPEAK_LIVE_AGENT` set:
`dotnet test -c Release --filter-trait "Category=Live" --ignore-exit-code 8`.

What this still does **not** exercise: the Genie full-result download endpoints and visualizations.
Those remain covered by contract tests only.

## Re-executing an expired result — 2026-08-06

Exercising `execute-query` against the live workspace corrected a wire assumption that a contract
test had encoded wrongly, and with it a real defect.

| Call | Response |
|---|---|
| `POST …/attachments/{id}/execute-query` | HTTP 200, `state: PENDING`, **no manifest, no rows** |
| `GET …/attachments/{id}/query-result` moments later | `state: SUCCEEDED`, rows present |

`execute-query` only *starts* the re-execution. The client returned that first acknowledgement, so
`ReExecuteQueryAsync` produced `null` and `export last` told the user to ask the question again —
while the warehouse work they had just paid for completed and was discarded. It now polls for the
rows.

The contract test covering this stubbed a *completed* response, which Databricks does not return.
A stub cannot catch an error in the stub, which is the general lesson and the reason
`A_re_executed_query_returns_its_rows` is a **live** test rather than another fixture.

Still unreached: the `QUERY_RESULT_EXPIRED` state that triggers recovery. Databricks expires the
cache on its own schedule, hours later, and there is no way to force it — so the recovery is
verified, and the condition it recovers from is simulated.

## `chat`, verified live — 2026-08-06

The one path that resists automation entirely: the REPL refuses to start without an interactive
terminal, by design, so no CI job or agent session can drive it. It was run by hand instead.

| Path | Result |
|---|---|
| REPL start against a live Agent | Banner, Agent name, prompt |
| A question | Answer text plus a rendered result table |
| **A follow-up** — "and break that down by quarter" | Kept its context and re-grouped the same figures by quarter. This is the behaviour the whole project is built around, and it had never been observed end to end before |
| `/sql` | Printed the generated SQL for the follow-up, not the first question |
| `/exit` | Clean exit |

`docs/assets/transcripts/chat.txt` is that session, with the workspace's identifiers replaced by
the synthetic ones the other transcripts use. It was previously reconstructed from string
literals; it is now captured output like every other transcript here.

Not exercised: `/new`, `/agents`, `/use`, `/result`, `/export`, `/thumbs-up`, `/thumbs-down`.

## Local verification — 2026-08-05

Not a live workspace. Recorded because the packaged surface is the only surface a user meets, and
"it built" is not evidence that it packaged.

| Check | Result |
|---|---|
| Full suite, `Category!=Live` | 224 tests, 0 failed, across five projects |
| Build under warnings-as-errors | Clean |
| `dotnet format --verify-no-changes` | Clean |
| `dotnet restore --locked-mode` | Succeeds; no lock file drift |
| Packaged tool installed to an isolated `--tool-path` | `lakespeak --version` → `0.1.0+b934d6b…`, matching the merge commit |
| Throwaway consumer against `LakeSpeak.Genie.0.1.0.nupkg` | Compiles and reads the new `MaxResultRows`, default `100000` — proving the new public member is in the *package*, not just the build output |

## Chunked result assembly — mechanism verified live, 2026-08-05

The client follows `next_chunk_internal_link` to assemble a chunked result
([ADR 0004](decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md)).
Every assumption that design rests on was checked against the live Azure workspace.

**The permission question is answered: yes.** This was the named unknown — whether a caller may
read the remaining chunks of a statement Genie executed on their behalf, given the link resolves to
`/api/2.0/sql/statements/…`, which is not a Genie path.

| Probe | Result |
|---|---|
| Genie `start-conversation` → completed message → `query.statement_id` | `01f19102-25bf-…` |
| `GET /api/2.0/sql/statements/{that id}` with the caller's own token | **HTTP 200**, manifest, `SUCCEEDED` |
| `GET .../result/chunks/0` for that Genie-executed statement | **HTTP 200**, rows returned |

**The wire contract behaves as the client assumes.** A deliberately chunked statement
(`SELECT id, repeat('x',120) FROM range(60000)`, run through the Statement Execution API so no
table was created):

| Observation | Value |
|---|---|
| `manifest.total_chunk_count` | `2` |
| `manifest.truncated` on a merely-chunked result | **`false`** — the original defect's premise, confirmed rather than assumed |
| Chunk 0 `row_count` | `41250` of `60000` |
| `next_chunk_internal_link` | `/api/2.0/sql/statements/{id}/result/chunks/1` — workspace-relative, the shape the client's host validation accepts |
| Following that link | HTTP 200, `row_offset: 41250`, `row_count: 18750`, no `next_chunk_index` |
| Assembled total | `41250 + 18750 = 60000`, matching `total_row_count` exactly |

That covers the link's shape, the chunk response's shape, the loop's termination condition, and the
row arithmetic the truncation flag depends on.

**Genie composition probe, 2026-09-01:** an existing AWS Genie Agent generated a 1,000-row result
with three repeated review-text columns. The SQL Statement manifest reported four chunks,
`truncated: false`, and 1,000 total rows. The Genie query-result response returned 316 rows with
`next_chunk_index: 1`, but omitted `next_chunk_internal_link`; the SQL Statement response included
the link. That live difference exposed why the original client could not complete the composition.

LakeSpeak now constructs the documented workspace-relative chunk endpoint from the statement id
and next index when Genie omits the link. The fixed client returned all 1,000 rows with
`IsTruncated: false`, closing [#54](https://github.com/ivanvyd/LakeSpeak.NET/issues/54). All
conversations created for the probes were deleted afterward.

Contract tests continue to cover the paths a live run cannot reach on demand: an unreachable chunk,
a repeated link, a link resolving off-workspace, and the row cap.

## What has been verified, and how

| Area | Evidence |
|---|---|
| Message status mapping, all ten values | Unit tests against the enum read from the generated SDK |
| Conversation lifecycle, polling, backoff, cancellation | Contract tests against a local HTTP server |
| Query result shape; decimals and nulls surviving unchanged | Contract tests |
| Truncation reported distinctly from local row limits | Contract tests and rendering code |
| HTTP failure mapping to typed kinds and exit codes | Contract tests, plus the CLI run by hand |
| Credential redaction, both signature fields | Unit tests using realistic JSON payloads |
| Question Pack validation, including path traversal | Unit tests, plus the CLI run by hand |
| CLI parsing, help, exit codes, `config show` leaking nothing | The built binary run by hand |
| Chunked result assembly, and every way it can stop short | Contract tests against a stubbed multi-chunk server. The complete Genie composition was verified live on AWS on 2026-09-01: four chunks, 1,000 rows assembled, no truncation. The probe also found and fixed Genie's omission of `next_chunk_internal_link` from its query-result response |
| Documented CLI commands still existing | A test parses every fenced `lakespeak …` example in this repository against the real command tree |
| The packaged public API, as opposed to the build output | A consumer project compiled against the `.nupkg` from `./artifacts`, 2026-08-05 |
| Unattended service-principal auth (OAuth M2M) | Contract tests against a stubbed token endpoint cover: Basic auth, form parameters, response shape, expiry, refresh, `invalid_client` mapping, non-JSON error bodies, and concurrent first-callers sharing one fetch. Valid credentials were verified against the AWS workspace on 2026-09-01: [main-branch GitHub Actions run 33516431819](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33516431819) passed all 9 live tests using native M2M from runtime-bearing revision `6b27140` |
| Against real Databricks | See "Live verification" above. The suite is re-runnable from `Actions → Live smoke (Databricks Genie)` and weekly behind `secrets.DATABRICKS_CLIENT_ID`, `secrets.DATABRICKS_CLIENT_SECRET`, `vars.LAKESPEAK_LIVE_HOST` and `vars.LAKESPEAK_LIVE_AGENT`. On 2026-09-01 the M2M-backed workflow passed all 9 live tests against AWS. Multi-chunk composition was also verified separately: four chunks and all 1,000 rows returned without truncation. Full-result download endpoints, visualizations and forced `QUERY_RESULT_EXPIRED` recovery remain contract-test-only |

## How to update this file

Add a row when you run something, naming what you ran it against. Remove a row if the evidence
stops being true. An entry with no evidence behind it is worse than a missing entry, because it
gets believed.

## Live run — 2026-08-01, second pass

Re-run of the full live suite (8 tests) against the same Azure Databricks workspace after the
Agent-resolution change that fetches an id directly instead of paging the listing. All 8 passed,
which is the point of re-running it: that change alters how every `ask`, `chat` and `pack run`
finds its Agent, and a contract test cannot tell you whether a real workspace agrees.

Still not exercised live, with the reason each resists it, are the three paths listed under v0.1 in
[ROADMAP.md](../ROADMAP.md).

## Live verification, AWS — through 2026-09-01

Run against a nonproduction AWS Databricks workspace. The initial 2026-08-29 pass used a PAT from
the maintainer's default profile. The 2026-09-01 pass used LakeSpeak's native OAuth M2M provider
with a dedicated test service principal. The Agent names, workspace hostname, and profile names are
omitted because they are not needed to reproduce the test shape.

| Path | Result |
|---|---|
| `lakespeak auth check` against AWS | Profiles were discovered, a token was obtained without being printed, the workspace answered, and 3 Agents were visible |
| `lakespeak ask` against a configured test Agent | The real response contained a three-row result over synthetic data and rendered successfully |
| Wire shape on AWS | Identical to Azure. The PAT path works unchanged. Native OAuth M2M was verified through all 9 live tests in [main-branch GitHub Actions run 33516431819](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33516431819). |
| Chunk composition | A wide 1,000-row query produced four chunks. Genie advertised `next_chunk_index` but omitted its link; LakeSpeak constructed the statement chunk endpoint and returned all rows with no truncation. |

The CI matrix is `ubuntu-latest`, `windows-latest`, `macos-latest`. The Azure verification ran on
the maintainer's primary machine. The first AWS verification ran locally on 2026-08-29 with the
same `lakespeak` v0.2.0 binary; the 2026-09-01 M2M verification ran on GitHub's Ubuntu runner from
runtime-bearing revision `6b27140` of the merged v0.3.1 candidate on `main`. The two workspaces
answer the same wire contract.

What remains untested on AWS:

- `chat` REPL — the REPL refuses to start without an interactive terminal, by design.
- `pack run` against a third-party-defined Question Pack — the bundled `daily-brief.yaml` references
  Azure-only table names and the run failed with `FileNotFoundException` on AWS, which is a question-pack
  authoring issue, not a wire-shape one.

## Local verification — 2026-09-01 (v0.3.1)

The v0.3.1 release candidate was checked as a package, not only as source:

| Check | Result |
|---|---|
| Locked restore and release build | Clean; 0 warnings and 0 errors |
| Full non-live suite | Explicit target runs: 264/264 passed on `net10.0`; 165/165 passed on `net8.0`. The tag workflow's default multi-target selection separately passed 417/417 |
| Formatting and dependency audit | `dotnet format --verify-no-changes` clean; no vulnerable direct or transitive packages reported |
| `LakeSpeak.Genie.0.3.1.nupkg` | Contains both `lib/net8.0/` and `lib/net10.0/` assemblies and XML documentation |
| `LakeSpeak.Cli.0.3.1.nupkg` | Installed to an isolated tool path; `lakespeak --version` returned `0.3.1` with commit metadata |
| Missing M2M secret | [Disposable-repository run 33524613970](https://github.com/ivanvyd/LakeSpeak.NET-live-smoke-negative-20260901/actions/runs/33524613970) failed at the credential guard; restore and tests were skipped |
| Pre-release independent review | Correctness, security, performance and structure found no production-code defect. Requirements review found stale roadmap claims; correctness review found two checklist reproducibility defects. The release-prep change corrected them |
| Post-release independent review | A second correctness, structure, security and requirements panel found no production-code defect. It found the stale 258-test count, an unpinned CycloneDX install, invalid public-NuGet attestation guidance and an ambiguous version-specific checklist. [PR #94](https://github.com/ivanvyd/LakeSpeak.NET/pull/94) corrected all four; [rehearsal 33537144267](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537144267) and final `main` [CI](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537586581) and [Security](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537586600) passed |

The current runtime and `live-smoke.yml` are identical to revision `6b27140`, whose native-M2M
AWS run passed all nine live tests. GCP remains untested and issue #52 remains open by decision.

## Local verification — 2026-08-31

The packaging changes for v0.3.0 (multi-targeting the library family, MTP migration, the
post-ship-review resilience fix). What reaches a consumer is what was checked, not what the
build output happened to look like.

| Check | Result |
|---|---|
| Full suite, `Category!=Live`, both TFMs, all three OSes | Green on `ubuntu-latest`, `windows-latest` and `macos-latest`, for `net8.0` and `net10.0` |
| Build under warnings-as-errors, both TFMs | Clean on all six cells |
| `dotnet format --verify-no-changes` | Clean on all six cells |
| `dotnet list package --vulnerable --include-transitive` | No moderate-or-higher advisories on either TFM |
| `dotnet restore --locked-mode` | Clean on all three OSes (lock files unchanged for the new matrix) |
| `dotnet pack` of `LakeSpeak.Genie` | `lib/net8.0/LakeSpeak.Genie.dll` and `lib/net10.0/LakeSpeak.Genie.dll` both present in the nupkg |
| `dotnet pack` of `LakeSpeak.Cli` | `tools/net10.0/any/` populated, `lakespeak.dll` present |
| Packaged tool installed to an isolated `--tool-path` | `lakespeak --version` → `0.3.0`, matching the release tag |
| Throwaway consumer against `LakeSpeak.Genie.0.3.0.nupkg` with `<TargetFramework>net8.0</TargetFramework>` | Compiles, instantiates `GenieClient`, resolves the registered `IGenieTokenProvider`. Proves the `net8.0` package is the *package*, not just the build output |
| Resilience contract on net8 — exception path | `A_connection_refused_start_conversation_is_never_retried` passes: a transient `HttpRequestException` on `start-conversation` is not retried. The pre-fix code retries 4 times in well under the Genie timeout; the fix retries 0 |
| Resilience contract on net8 — response path | `A_failed_start_conversation_is_never_retried` passes: a 503 on `start-conversation` is not retried |
