# SOC 2 control mapping

## What this document does and does not claim

LakeSpeak.NET implements technical controls that map to the SOC 2 Trust Services Criteria. It
cannot itself hold a SOC 2 report.

SOC 2 is an examination of controls **at a service organization**. It applies to an organisation,
not to software, and it produces a *report* rather than a certificate. A tool can support an
organisation's controls; it cannot hold the attestation. Any project claiming to be "SOC 2
compliant" or "SOC 2 certified" is either confused or lying, and the phrasing is wrong even for a
company that holds a report.

**Wording this project uses:** "Implements technical controls that map to the SOC 2 Trust
Services Criteria."

**Wording this project will not use:** "SOC 2 compliant", "SOC 2 certified", "SOC 2 ready" as a
badge, or any graphic implying attestation.

## Scope, and why it is narrower than a platform's

LakeSpeak is a client. It runs on a workstation or a CI runner, holds no data at rest, has no
users of its own, and stores no credentials. That removes whole categories an application would
own — there is no access-provisioning story here because there are no LakeSpeak accounts, and no
backup-and-restore story because there is nothing to restore.

What remains is the part a client genuinely controls: what it does with your credentials, what it
does with data on its way past, whether its output can be trusted to be faithful, and whether the
artifact you install is the one that was reviewed.

Security is the only mandatory Trust Services category, realised through common criteria CC1 to
CC9. The rest are elective. CC1 (control environment) and CC3 (risk assessment) are
organisational and cannot be shipped in a package; they are listed as gaps, because being
explicit about what this does not do is what makes the rest credible.

## Mapping

| Control | Criteria | Implementation and evidence |
|---|---|---|
| No credential persistence | CC6.1 | Tokens are brokered through the Databricks CLI and held in memory for the process lifetime. LakeSpeak writes no credential store, and its config file has no field that could hold one. Evidence: `DatabricksCliTokenProvider`, and `config show` printing no secrets. |
| Credential non-disclosure | CC6.1, CC6.6 | Tokens never enter a URL, a process argument, or a log. The CLI is invoked with an argument vector and `UseShellExecute = false`, so a profile name from a config file or Question Pack cannot reach a shell. Evidence: the redaction suite, including regression tests for both `download_id_signature` and `statement_id_signature`. |
| Defence in depth on disclosure | CC6.1 | `GenieException` scrubs its own message at construction, so a token cannot reach a caller's log even if a call site forgets. A denylist cannot be complete, so it is the last line rather than the only one. |
| Encryption in transit | CC6.7 | `https` is required; `GenieClientOptions.Validate` refuses any other scheme, because a bearer token over `http` is a disclosed token and the mistake is otherwise silent. Presigned result links are fetched **without** the `Authorization` header so the Databricks credential is not handed to blob storage. |
| Least privilege | CC6.3 | LakeSpeak cannot widen access. Every request carries the selected caller identity: an interactive user or an OAuth M2M service principal. Unity Catalog decides what that identity sees; LakeSpeak provides no impersonation or privilege-elevation path. |
| Processing integrity | PI1.1, PI1.4 | Result cells are carried as strings from the wire to the output, so a `DECIMAL` is never round-tripped through a floating-point type on its way to a report. Completeness is reported conservatively: a result is flagged truncated if Databricks truncated it, **or** if it continues into a chunk this version does not read, **or** if fewer rows arrived than the manifest advertises. Local display limits are reported as a separate, differently-worded message so a capped terminal view is never mistaken for a capped export. Evidence: contract tests asserting decimals and nulls survive unchanged, and that each of the three incompleteness signals sets the flag. |
| Output faithfulness | PI1.2 | Machine formats carry a versioned schema (`schemaVersion`), results go to stdout and diagnostics to stderr, and exit codes are stable and documented. A consumer can tell success from failure without parsing prose. |
| Injection resistance | CC6.6 | Values returned by Databricks are untrusted for rendering: control characters are replaced at the boundary so a crafted cell cannot move the cursor, clear the screen, or spoof this tool's prompt. CSV output guards against a leading `=`, `+`, `-` or `@` being executed as a spreadsheet formula. |
| Untrusted input handling | CC6.6, CC7.1 | Question Packs are data, validated against a published JSON Schema before anything runs. Output paths that resolve outside the pack directory are rejected, and unknown keys fail rather than being silently ignored. Evidence: the loader test suite, including traversal cases. |
| Change management | CC8.1 | Every change lands through a pull request with required status checks. Architecture and public-API changes require an ADR in the same PR. Evidence: the Git history, the branch protection settings, and `docs/decisions/`. |
| Vulnerability management | CC7.1, CC9.1 | Dependencies are centrally pinned with lock files; CI restores in `--locked-mode`, so a drifted transitive version fails the build rather than shipping. A moderate-or-higher advisory fails CI. CodeQL on push and pull request, Dependabot weekly, OpenSSF Scorecard, and secret scanning with push protection. |
| Supply chain integrity | CC7.1, CC8.1 | GitHub Actions pinned by commit SHA. Deterministic builds. Releases carry an SBOM, SHA-256 checksums, and build provenance attestation. Publishing requires a protected environment, so it is a decision rather than a side effect of pushing a tag. |
| Confidentiality of data in transit through the tool | C1.1 | No question, answer, or query result is written anywhere except a file the user explicitly asks for. Nothing is cached to disk. Conversation history stays in Databricks rather than being copied locally. |
| Confidentiality of exports | C1.2 | Export warns that the file contains governed data and refuses to overwrite without confirmation. The documentation is explicit that a written CSV is thereafter the user's responsibility, and that CI logs are usually readable by everyone with repository access. |
| Traceability | CC7.2 | Conversation and message ids are preserved on every response so an answer can be traced back to its conversation in Databricks, where the workspace audit log records it. LakeSpeak does not keep an audit log of its own; the system of record is Databricks. |
| Incident response | CC7.3, CC7.4 | `SECURITY.md` with a private reporting channel, stated response expectations, and GitHub Security Advisories as the coordinated-disclosure mechanism. |
| Availability of the dependency | A1.1 | Timeouts, cancellation and typed failures on every path, so a hung warehouse surfaces as a `PollingTimeout` naming the last observed state rather than an indefinite hang. Retries never apply to authorization failures. |

## What this does not cover

| Gap | Criteria | Why |
|---|---|---|
| Control environment: org structure, background checks, security training, board oversight | CC1.1–CC1.5 | Organisational. No software can provide it. |
| Risk assessment and vendor risk management | CC3.1–CC3.4, CC9.2 | The adopting organisation owns this. The SBOM published with each release is a starting point for the vendor list and nothing more. |
| Logical access provisioning and review | CC6.2, CC6.3 | LakeSpeak has no accounts. Identity and Genie Agent access live in Databricks and its identity provider, and their joiner/mover/leaver process is the control. |
| Physical access | CC6.4, CC6.5 | The cloud provider's responsibility, covered by their own report. |
| Backup and restore | A1.2, CC7.5 | LakeSpeak stores nothing to restore. Conversations live in Databricks. |
| Monitoring of controls, internal audit | CC4.1, CC4.2 | Organisational process. |
| Correctness of Genie's answers | PI1.1 | Out of any tool's reach. LakeSpeak preserves the generated SQL, the source metadata and the message ids **so that a human can check**, and states plainly that a plausible answer can still be wrong. |

## A note on the last row

It is the most important one. Everything above concerns whether LakeSpeak faithfully carries data
and credentials. None of it makes a natural-language answer correct. A control mapping that
implied otherwise would be worse than no mapping, because it would invite someone to treat a
generated number as audited.
