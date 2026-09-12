# Releasing

How a LakeSpeak release is cut. Written down because a release process that lives in one person's
head is a release process that stops when they do.

## What a release produces

| Artifact | Where it goes |
|---|---|
| `LakeSpeak.Genie` | NuGet — the library |
| `LakeSpeak.Cli` | NuGet — the `lakespeak` dotnet tool |
| `lakespeak-<version>-win-x64.zip` | GitHub release — self-contained, no SDK needed |
| `lakespeak-<version>-linux-x64.zip` | GitHub release |
| `lakespeak-<version>-osx-arm64.zip` | GitHub release (built, **not tested** — see `docs/compatibility.md`) |
| `SHA256SUMS.txt` | GitHub release |
| `sbom.json` | GitHub release — CycloneDX, the vendor list an adopter's review will ask for |
| Build provenance attestation | GitHub Attestations API and release bundle; covers submitted `.nupkg` files and release archives |

## Versioning

Semantic versioning. **Before `v1.0`, a minor version may break the API** — that is what `0.x`
means, and the README says so rather than leaving people to discover it.

The version comes from one place per situation:

- **A signed release-tag run** at `v1.2.3` → version `1.2.3`.
- **A manual branch rehearsal** with the `version` input → that value, after SemVer validation.
- **A manual branch rehearsal with no input** → `0.0.0-dev.<run number>`.

Manual runs never publish. A manual rehearsal run at a release tag derives its version from that
tag and rejects a conflicting `version` input.

`VersionPrefix` in `Directory.Build.props` is the local-development default only. CI overrides it.

## Rehearse first

The release workflow is manually runnable, and manual runs do **not** publish. Use that.

1. Actions → **Release** → *Run workflow*.
2. Optionally set **version** to the version you intend to cut.
3. Run it.

That builds, runs the full non-live test suite, packs, publishes the three self-contained
binaries as workflow artifacts, generates the SBOM and attests provenance — everything a real
release does except pushing to NuGet and creating a GitHub release. If the rehearsal is red, the
release would have been red.

Download the artifact and install the tool locally before trusting it:

```bash
dotnet tool install --global --add-source ./artifacts LakeSpeak.Cli --version <version>
lakespeak --version
```

CI already does this on every PR (the `tool-smoke` job), but doing it by hand once before a real
release is cheap.

## Signed tags

The release workflow verifies the tag's signature before it builds anything, against the public
keys in the current `main` branch's [`.github/allowed_signers`](.github/allowed_signers). It also
peels the annotated tag, requires that commit to equal the checked-out commit, and requires the tag
commit to be contained in `origin/main`. Only a signed `v*` tag **push** can authorize the publish
job; manually dispatched runs remain rehearsals, including runs dispatched at a tag.

This exists because the provenance attestation answers a different question than people assume. It
proves **what** built an artifact — this workflow, this repository, this commit. It cannot prove
**who** authorised the release: anyone able to push a tag starts the workflow, and the attestation
on the result would be perfectly valid. The signing key is the one credential GitHub does not hold,
so requiring a signed tag is what makes a stolen GitHub account insufficient by itself.

Signing uses SSH, not GPG — the same key already used for commits, so there is no second key to
manage:

```bash
git config gpg.format ssh
git config user.signingkey ~/.ssh/id_ed25519.pub
git config tag.gpgSign true
```

Two consequences worth knowing before relying on this.

**A lost key blocks releases** until a new public key is committed to `.github/allowed_signers`.
That is the trade: the control is only as available as the key. Keep a second maintainer key in
that file if the project ever gains one.

**It is not absolute.** Someone holding the GitHub account could open a pull request removing the
verification step and merge it. Signing makes that a multi-step attack recorded in git history
rather than a single silent tag push, which is the realistic protection available here.

## Cut the release

### Prerequisites, once

> **Verified working on 2026-08-01** by publishing `0.1.0-preview.1`. Two fields were wrong on
> the first attempt and each failed the token exchange *before* anything was pushed, which is the
> behaviour to rely on: a mismatched policy costs a re-run, never a bad package. The failure was
> `Workflow mismatch for policy 'LakeSpeak.NET': expected 'publish.yml', actual 'release.yml'` —
> the error names both sides, so read it literally rather than guessing which field is wrong. The trusted publishing policy exists on nuget.org, the
> `NUGET_USER` secret is set, and the `nuget` environment exists with `ivanvyd` as a required
> reviewer. The steps below record what was done, so it can be rebuilt or audited.
>
> One thing worth knowing if you ever recreate this: a workflow naming an environment that does
> **not** exist does not fail — GitHub creates it implicitly with no protection rules. An absent
> environment is silently permissive, not loud.

Publishing uses **trusted publishing**, not a stored API key. GitHub mints a short-lived OIDC
token, nuget.org verifies it against a policy naming this exact repository and workflow, and
returns a temporary key valid for one hour. Nothing long-lived is ever stored in the repository,
so there is no key to leak, rotate, or accidentally scope too widely.

**1. Create the trusted publishing policy on nuget.org.**

Log in to nuget.org → your username → **Trusted Publishing** → add a policy:

| Field | Value |
|---|---|
| Repository Owner | `ivanvyd` |
| Repository | `LakeSpeak.NET` |
| Workflow File | `release.yml` — the file name only, **not** `.github/workflows/release.yml` |
| Environment | `nuget` — set, so the policy only accepts tokens minted from that environment |

**Environment** is optional. nuget.org only checks the token's `environment` claim [when the
policy supplies a
filter](https://github.com/NuGet/Home/blob/dev/accepted/2024/trusted-publishers-oidc-for-nuget-push.technical.md),
so leaving it blank would still accept a job running under `environment: nuget`. It is set here
because filling it in narrows the policy: a token minted from any other environment is rejected.
Owner, repository and workflow file are checked either way.

Because it *is* set, the publish job must keep `environment: nuget`. Removing it would change the
token's claim and the policy would stop matching.

The policy is owned by you or by an organisation, and applies to every package that owner owns.
If Trusted Publishing does not appear in your account, it has not been rolled out to you yet;
in that case fall back to an API key scoped to `LakeSpeak.*`.

**2. Add the `NUGET_USER` repository secret.**

Your nuget.org **profile name** — not your email address. It is not a credential; it is a secret
only so the workflow file does not hard-code an account name.

```bash
gh secret set NUGET_USER
```

**3. Create the `nuget` environment with a required reviewer.**

Settings → Environments → New environment → `nuget` → add yourself under *Required reviewers*.
Leave *Prevent self-review* off: as the only reviewer you would otherwise be unable to approve
your own release, which blocks publishing entirely rather than securing it.

Creating the environment alone changes nothing — the required reviewer *is* the gate. With it, a
tag push builds, tests, packs and attests, then stops and waits for you before anything reaches
NuGet.

**4. Verify all three before the first release.**

```bash
gh api repos/ivanvyd/LakeSpeak.NET/environments --jq '.environments[] | {name, rules: [.protection_rules[].type]}'
gh secret list
```

The first publish also completes the policy: for a new policy nuget.org records the GitHub
repository and owner IDs on first successful use, which is what stops someone deleting the repo,
recreating it under the same name, and publishing as if nothing changed.

### Steps

1. Update `CHANGELOG.md`. Move `Unreleased` entries under a new `## <version> — <date>` heading.
2. Confirm `docs/compatibility.md` reflects what has actually been verified for this version.
   An entry there with no evidence behind it is worse than a missing one.
3. Merge those to `main`.
4. Tag and push. **The tag must be signed** — the workflow refuses to build an unsigned one:

   ```bash
   git tag -a v1.2.3 -m "v1.2.3"
   git push origin v1.2.3
   ```

   `tag.gpgSign` is set locally to `true`, so `git tag -a` signs without `-s`. Check before
   pushing if you want to be sure:

   ```bash
   git -c gpg.ssh.allowedSignersFile=.github/allowed_signers verify-tag v1.2.3
   ```

5. The workflow runs. If the environment gate is configured (see prerequisites), it stops
   there for approval — otherwise it publishes straight away.
6. Check the GitHub release: three binaries, checksums, SBOM, generated notes.

### Retry a failed publication

Do not start a manual run. Open the original tag-triggered workflow run and choose **Re-run failed
jobs**, or use:

```bash
gh run rerun <run-id> --failed
```

GitHub documents that a re-run keeps the original event's `GITHUB_SHA` and `GITHUB_REF`, so the
retry remains bound to the same signed tag and commit. The successful build job's artifacts are
reused by the retried publish job; no replacement version or tag is supplied. Re-runs are available
for 30 days after the initial run. See [Re-running workflows and
jobs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).

## If something goes wrong

**NuGet does not allow unpublishing.** A package can be deprecated or delisted, never removed.
That is why the rehearsal step exists, and why configuring the environment gate is worth
doing before the first release rather than after the first mistake.

- **Wrong version published** → publish a corrected higher version, then delist the wrong one.
  Do not attempt to reuse the version number; NuGet will reject it and `--skip-duplicate` will
  silently succeed without publishing anything.
- **Tag pushed too early** → delete the tag (`git push --delete origin v1.2.3`) *before* approving
  the environment gate. After approval, the only path forward is a new version.
- **Release job red after publish** → the packages are already on NuGet. Fix the GitHub release
  by hand rather than re-running the whole workflow.

## What is deliberately not automated

There is no auto-release on merge, no release-please, no auto-generated version from commit
messages. For a project this size those add a machine that has to be understood before a release
can be made, which is the opposite of what this file is for.
