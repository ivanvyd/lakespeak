# Security policy

## Reporting a vulnerability

Report privately through
[GitHub Security Advisories](https://github.com/ivanvyd/LakeSpeak.NET/security/advisories/new). Do not
open a public issue for a vulnerability.

This is a solo-maintained project. Expect an acknowledgement within 5 working days and an initial
assessment within 10. If a fix is warranted, you will be credited in the advisory unless you ask not
to be. If you have not heard back in 10 working days, please assume the message was missed and
escalate by opening a public issue that says only that you are waiting on a security response —
with no details.

## Supported versions

Until `v1.0`, only the latest released minor version receives fixes.

## What LakeSpeak protects

**Access tokens are never persisted.** LakeSpeak brokers short-lived OAuth tokens through the
Databricks CLI and holds them in memory for the life of the process. It writes no credential store,
and no token is written to its configuration file.

**Tokens are never passed as process arguments.** Command lines are readable by other users on the
same host on most platforms.

**Authorization headers are redacted** in all diagnostic output, including `--verbose`, and in
exception messages. Scheme-prefixed credentials (`Authorization: Bearer <token>`) are covered —
an earlier pattern redacted only the scheme word and left the credential, which is why the
redaction suite now tries to defeat itself rather than confirming it works on convenient shapes.

**Presigned result URLs are fetched without the `Authorization` header.** Sending a Databricks
bearer token to blob storage would leak it to a third party. Databricks rejects such requests with
HTTP 400, so the mistake fails loudly rather than silently — but the client must not rely on that.

**Arguments to the Databricks CLI are passed as an argument vector, never through a shell.** Agent
names, profile names and questions are attacker-influenced in the sense that they may come from a
Question Pack or a script; none of them reaches a shell interpreter.

**Question Packs are data, not code.** They are validated against a published JSON Schema. A pack
cannot execute a command, read an arbitrary file, or widen the permissions of the identity running
it.

**Terminal output is sanitized.** Genie returns model-generated text and query results drawn from
your data. Both are untrusted for rendering purposes: ANSI escape sequences and control characters
are stripped before anything reaches your terminal, so a crafted cell value cannot rewrite your
screen or spoof a prompt.

**Question Pack output paths are checked before remote work.** Pack-declared paths must stay below
the pack directory and neither pack paths nor explicit `--output` paths may traverse a symbolic
link or junction. Reports are staged and atomically installed; an existing file is not replaced
unless `--force` was supplied.

## What LakeSpeak does not protect against

**It cannot make Genie answers correct.** Generated SQL can be wrong in ways that look right.

**It cannot widen or narrow your Unity Catalog permissions.** You see what your identity can see. If
that is more than you expected, that is a workspace governance question, not a LakeSpeak one.

**It cannot protect data after you export it.** An exported CSV is an ordinary file with no
protection of its own.

**It cannot keep results out of CI logs.** If you run `lakespeak ask` in a pipeline, the answer goes
wherever that pipeline's output goes. Treat job logs as readable by everyone with repository access.

**It does not defend against a malicious Databricks workspace.** A workspace you authenticate to can
return whatever it likes. LakeSpeak validates response shape but trusts response content.

## Threat model

Recorded in [docs/security/threat-model.md](docs/security/threat-model.md).

## Supply chain

- Dependencies are centrally pinned with lock files; CI restores in `--locked-mode`.
- A moderate-or-higher advisory in any dependency, including transitive, fails the build.
- GitHub Actions are pinned by commit SHA.
- Releases carry an SBOM, SHA-256 checksums, and build provenance attestation.
- Publishing to NuGet requires a protected environment, so it is a decision rather than a side
  effect of pushing a tag.
- Live integration tests never run for pull requests from forks, because they need workspace
  credentials.
- Every publishing run must come from a signed `v*` tag whose peeled commit equals the workflow
  checkout and is contained in `origin/main`. Manual workflow runs can rehearse a build but cannot
  publish. A key allowed by the current `main` branch is therefore required to authorize a new
  release identity.

## Verifying a release

Every GitHub release binary carries a [SLSA build provenance
attestation](https://slsa.dev/spec/v1.0/provenance), signed through Sigstore using GitHub's OIDC
identity. No signing key exists to be stolen, and the attestation binds each archive to the
workflow, commit and repository that produced it.

Checking it takes one command and needs only the [GitHub
CLI](https://cli.github.com):

```bash
gh attestation verify lakespeak-0.3.1-linux-x64.zip --repo ivanvyd/LakeSpeak.NET
```

NuGet.org adds its repository signature after LakeSpeak submits each package. That changes the
`.nupkg` archive hash, so the public NuGet download cannot match the pre-submission build
attestation. Verify the public package's NuGet repository signature instead:

```bash
dotnet nuget verify --all LakeSpeak.Cli.0.3.1.nupkg
```

The release workflow also attests the unsigned package bytes it submits, but NuGet's post-upload
signature means those hashes are not a verification interface for downloaded packages. A passing
archive attestation proves the GitHub release file came from this repository's release workflow. A
passing NuGet verification proves the package carries NuGet.org's repository signature; it does not
by itself prove the source commit.

> **GitHub archive attestations apply from 0.1.1.** In `0.1.0` only the submitted `.nupkg` files
> were attested, so verification of a downloaded release binary fails. The binaries were built by
> the same workflow run, and their digests remain available in that release's `SHA256SUMS.txt`.

The attestation answers *what built this*. **Who authorised it** is a separate question, answered
by the release tag: before it builds, the workflow verifies the tag's SSH signature against the
current `main` branch's [`.github/allowed_signers`](.github/allowed_signers), checks that the peeled
tag commit is the commit it checked out, and requires that commit to be contained in `origin/main`.
Manual dispatches are nonpublishing rehearsals. A publishing run therefore
cannot change the version or source commit without an allowed signing key. A GitHub account could
re-run or replay an existing signed tag, but that remains bound to the same immutable package
version and source. Someone able to change this workflow and merge the change could remove the
control; the signed tag makes that a separate, reviewable repository change rather than a property
of the GitHub account alone. You can check any release tag yourself:

```bash
git -c gpg.ssh.allowedSignersFile=.github/allowed_signers verify-tag v0.1.0
```

To check the binaries against their published digests instead, from the directory you downloaded
them into:

```bash
sha256sum -c SHA256SUMS.txt
```

> **Also applies from 0.1.1.** `0.1.0`'s `SHA256SUMS.txt` additionally lists the four `.nupkg` and
> `.snupkg` files, which are published to NuGet rather than attached to the release — so that
> command reports four missing files there. The three entries for the release binaries are correct
> and can be checked individually with `sha256sum -c --ignore-missing SHA256SUMS.txt`.

`sbom.json` on each release is a CycloneDX bill of materials, which is usually what a security
review asks for before any of the above.

This is worth doing precisely because this project is small. A widely-used package has many eyes
on it; a new one from an author you have not heard of has yours.
