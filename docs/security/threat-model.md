# Threat model

What LakeSpeak is trying to prevent, what it is not, and where the boundaries sit.

## What this is

A CLI and library that runs on a workstation or a CI runner. It holds no data at rest, has no users
of its own, exposes no network service, and stores no credentials. It authenticates as **you** and
carries data between Databricks and your terminal or a file.

That shape decides the threat model. There is no server to attack, no session to hijack, and no
credential store to exfiltrate. What is left is: the credential passing through, the data passing
through, the untrusted content coming back, and the artifact you installed.

## Trust boundaries

| Boundary | Trusted? | Why it matters |
|---|---|---|
| The user running the tool | Yes | They already hold the Databricks credential. LakeSpeak grants them nothing new. |
| The Databricks workspace | Partially | Trusted to authenticate and authorise. **Not** trusted for content: responses are model-generated and table-derived. |
| A Question Pack file | **No** | Can arrive in a pull request or a shared repository. Treated as untrusted input. |
| The local filesystem | Yes for reading config, **no** for write targets | Export paths are attacker-influenceable through packs. |
| The Databricks CLI binary | Yes | If it is compromised, so is `databricks` itself; nothing LakeSpeak does helps. |
| The terminal | Sink | Cannot validate what it renders, so LakeSpeak must not send it anything dangerous. |

## Threats and what is done about them

### T1 — Access token disclosure

*A token reaches a log, a CI transcript, a bug report, or another user's process list.*

The token is never persisted, never placed in a URL, and never passed as a process argument
(readable by other local users on most platforms). It is attached in exactly one place, a
`DelegatingHandler`, so no call site handles a raw token.

Authorization headers, both Databricks signature fields (`download_id_signature`,
`statement_id_signature`), and token-shaped strings are redacted from all diagnostic output.
`GenieException` scrubs its own message at construction, so a token cannot escape through an
exception even if a call site forgets.

A denylist cannot be complete, so it is the last line rather than the only one.

**Residual risk:** a token supplied via `DATABRICKS_TOKEN` is visible in the environment of the
process and to anything that can read it. That is inherent to environment credentials.

### T2 — Token disclosure to a third party

*The Databricks bearer token is sent somewhere that is not Databricks.*

Query results can arrive as presigned links to cloud storage. Sending the Databricks token to blob
storage would hand a credential to a third party. Those requests are issued **without** the
`Authorization` header. Databricks rejects such requests with HTTP 400, so the mistake would be
loud — but the client must not make it in the first place.

`https` is enforced on the workspace host. A bearer token over plain HTTP is a disclosed token, and
the mistake is otherwise silent.

### T3 — Command injection through a profile or Agent name

*A crafted profile name in a config file or Question Pack executes a shell command.*

The Databricks CLI is invoked with an argument vector and `UseShellExecute = false`. No
user-supplied value reaches a shell interpreter. There is no other subprocess execution anywhere in
the tool.

### T4 — Terminal injection from response content

*A table cell or a model-generated answer contains ANSI escapes.*

Genie returns model output and cells drawn from your data. Both are untrusted for rendering: a
crafted value can move the cursor, clear the screen, recolour later output, or draw something
resembling this tool's own prompt to solicit input.

Control characters are replaced at the rendering boundary, preserving only newline, carriage return
and tab. Sanitising once at the boundary is more reliable than auditing every write site.

### T5 — Formula injection into a spreadsheet

*An exported CSV opens in Excel and a cell executes.*

A cell beginning `=`, `+`, `-` or `@` is evaluated as a formula by common spreadsheet software. Such
values are prefixed with a single quote on export — visibly, not silently.

### T6 — Path traversal through a Question Pack

*A pack writes its report outside its own directory.*

Output paths are resolved against the pack's directory and rejected if they escape it, are
absolute, or traverse a symbolic link or junction. The command prepares the destination before it
starts warehouse work, keeps verified parent directories in place on Windows, rechecks them at the
write boundary on every platform, stages a complete sibling file, and atomically installs it. A
pack can arrive in a pull request, so `../../.ssh/authorized_keys` and a link supplied in the same
change are realistic inputs rather than hypotheticals.

Portable managed APIs do not provide a no-follow rename rooted at an already-open directory handle
on every supported Unix filesystem. A same-user process that can replace an ordinary parent during
the final rename system call remains outside this guarantee. A link or changed directory observed
at either managed boundary fails closed.

### T7 — Governed data leaking through artifacts

*Query results end up somewhere they should not.*

LakeSpeak writes nothing to disk except files you explicitly request, caches nothing, and copies no
conversation content into its config. Export warns that the file contains governed data and asks
before overwriting. Question Pack reports omit conversation and message ids by default, because
those identify a conversation containing governed data and reports get committed.

**Residual risk, and it is the largest one.** Once you export a file or run in CI, the data is
where you put it. Job logs are usually readable by everyone with repository access. No client-side
control can fix that; the documentation says so plainly rather than implying protection.

### T8 — Supply chain compromise

*The package you install is not the code that was reviewed.*

Dependencies are centrally pinned with lock files, and CI restores in `--locked-mode`, so a drifted
transitive version fails the build instead of shipping. A moderate-or-higher advisory fails CI.
GitHub Actions are pinned by commit SHA. Builds are deterministic. Releases carry an SBOM, SHA-256
checksums and build provenance attestation. Publishing requires a protected environment, so it is a
decision rather than a side effect of pushing a tag.

Secret scanning with push protection is enabled on the repository — it has already blocked one
commit, correctly, on a synthetic test fixture.

### T9 — Untrusted pull requests reaching credentials

*A fork PR runs live tests and exfiltrates workspace secrets.*

Live tests are excluded from the default run by trait and never execute for pull requests. Jobs that
need workspace credentials are gated on the PR originating from this repository.

### T10 — A wrong answer treated as authoritative

*Someone acts on a plausible but incorrect number.*

Not a security control, and listed here because it is the most likely real-world harm.

Generated SQL can be wrong in ways that read as correct. LakeSpeak preserves the SQL, the result and
the message ids so a person can check, and every Question Pack report carries a non-configurable
warning. It cannot judge correctness, and the SOC 2 mapping says explicitly that no control makes a
generated answer true.

## Out of scope

- **A malicious Databricks workspace.** A workspace you authenticate to can return anything.
  Response *shape* is validated; response *content* is trusted.
- **A compromised local machine.** If an attacker runs code as you, they have your credentials
  regardless.
- **Denial of service.** LakeSpeak exposes no service. Warehouse capacity is a Databricks concern.
- **Unity Catalog correctness.** LakeSpeak cannot widen or narrow what your identity can see.

## Reporting

See [SECURITY.md](../../SECURITY.md). Report privately; do not open a public issue.
