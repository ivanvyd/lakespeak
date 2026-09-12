#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/release.yml"
verifier="$repo_root/.github/scripts/verify-release-identity.sh"

fail() {
  printf 'release workflow policy test failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$verifier" ]] || fail "missing release identity verifier"

grep -Fq 'publish_authorized: ${{ steps.release_identity.outputs.publish_authorized }}' "$workflow" \
  || fail "the build job does not export the verified publishing decision"
grep -Fq 'bash .github/scripts/verify-release-identity.sh' "$workflow" \
  || fail "the release workflow does not invoke the identity verifier"
if grep -Fq 'steps.version.outputs.version' "$workflow"; then
  fail "the build still consumes the removed unverified version output"
fi
if grep -Fq "description: 'Publish to NuGet" "$workflow"; then
  fail "manual dispatch still exposes a publishing input"
fi
if grep -Fq 'inputs.publish' "$workflow"; then
  fail "the release workflow still trusts a manual publishing input"
fi

publish_job="$(sed -n '/^  publish:/,$p' "$workflow")"
grep -Fq "if: needs.build.outputs.publish_authorized == 'true'" <<<"$publish_job" \
  || fail "the publish job is not gated only by the verified publishing decision"

for boundary in 'NuGet/login@' 'dotnet nuget push' 'softprops/action-gh-release@'; do
  [[ "$(grep -Fc "$boundary" "$workflow")" -eq 1 ]] \
    || fail "expected exactly one $boundary publishing boundary"
  grep -Fq "$boundary" <<<"$publish_job" \
    || fail "$boundary appears outside the gated publish job"
done

fixture_root="$(mktemp -d)"
trap 'rm -rf "$fixture_root"' EXIT

remote="$fixture_root/remote.git"
work="$fixture_root/work"
trusted_key="$fixture_root/trusted"
untrusted_key="$fixture_root/untrusted"

git init --bare -q "$remote"
git init -q "$work"
ssh-keygen -q -t ed25519 -N '' -f "$trusted_key"
ssh-keygen -q -t ed25519 -N '' -f "$untrusted_key"

cd "$work"
git config user.name 'Release Fixture'
git config user.email 'release@example.com'
git config gpg.format ssh
git config user.signingkey "$trusted_key"
mkdir -p .github
printf 'release@example.com %s\n' "$(cat "$trusted_key.pub")" > .github/allowed_signers
printf 'fixture\n' > payload.txt
git add .github/allowed_signers payload.txt
git commit -q -m 'fixture release'
release_commit="$(git rev-parse HEAD)"
git tag -s v1.2.3 -m 'v1.2.3'
git remote add origin "$remote"
git push -q origin HEAD:refs/heads/main refs/tags/v1.2.3

invoke() {
  local event_name="$1"
  local ref="$2"
  local ref_type="$3"
  local ref_name="$4"
  local version="$5"
  local output="$fixture_root/output"
  : > "$output"

  GITHUB_EVENT_NAME="$event_name" \
  GITHUB_REF="$ref" \
  GITHUB_REF_TYPE="$ref_type" \
  GITHUB_REF_NAME="$ref_name" \
  GITHUB_RUN_NUMBER=42 \
  GITHUB_OUTPUT="$output" \
  INPUT_VERSION="$version" \
    bash "$verifier"
}

expect_failure() {
  local description="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    fail "$description unexpectedly succeeded"
  fi
}

invoke workflow_dispatch refs/tags/v1.2.3 tag v1.2.3 '' >/dev/null
grep -Fxq 'version=1.2.3' "$fixture_root/output" \
  || fail "a tag rehearsal did not derive its version from the tag"
grep -Fxq 'publish_authorized=false' "$fixture_root/output" \
  || fail "a nonpublishing tag rehearsal was authorized to publish"

invoke push refs/tags/v1.2.3 tag v1.2.3 '' >/dev/null
grep -Fxq 'publish_authorized=true' "$fixture_root/output" \
  || fail "a trusted tag push was not authorized"

expect_failure "branch push publication" \
  invoke push refs/heads/main branch main 1.2.3
expect_failure "tag/version mismatch" \
  invoke workflow_dispatch refs/tags/v1.2.3 tag v1.2.3 1.2.4

git checkout -q --detach "$release_commit"
printf 'later\n' >> payload.txt
git add payload.txt
git commit -q -m 'later commit'
unmerged_commit="$(git rev-parse HEAD)"
expect_failure "tag/source mismatch" \
  invoke workflow_dispatch refs/tags/v1.2.3 tag v1.2.3 ''

git checkout -q --detach "$release_commit"
printf 'main advanced\n' >> payload.txt
git add payload.txt
git commit -q -m 'advance main'
git push -q origin HEAD:refs/heads/main

git checkout -q --detach "$release_commit"
git tag v1.2.4
git push -q origin refs/tags/v1.2.4
expect_failure "lightweight unsigned tag" \
  invoke workflow_dispatch refs/tags/v1.2.4 tag v1.2.4 ''

git tag -a v1.2.7 -m 'v1.2.7'
git push -q origin refs/tags/v1.2.7
expect_failure "annotated unsigned tag" \
  invoke workflow_dispatch refs/tags/v1.2.7 tag v1.2.7 ''

git config user.signingkey "$untrusted_key"
git tag -s v1.2.5 -m 'v1.2.5'
git push -q origin refs/tags/v1.2.5
expect_failure "tag signed by an untrusted key" \
  invoke workflow_dispatch refs/tags/v1.2.5 tag v1.2.5 ''

git checkout -q --detach "$unmerged_commit"
git config user.signingkey "$trusted_key"
git tag -s v1.2.6 -m 'v1.2.6'
git push -q origin refs/tags/v1.2.6
expect_failure "signed tag outside the release branch" \
  invoke workflow_dispatch refs/tags/v1.2.6 tag v1.2.6 ''

shallow="$fixture_root/shallow"
git -c advice.detachedHead=false clone -q --depth 1 --branch v1.2.3 "file://$remote" "$shallow"
cd "$shallow"
invoke push refs/tags/v1.2.3 tag v1.2.3 '' >/dev/null
grep -Fxq 'publish_authorized=true' "$fixture_root/output" \
  || fail "a trusted tag failed from an actions/checkout-style shallow clone"

invoke workflow_dispatch refs/heads/main branch main 1.2.3 >/dev/null
grep -Fxq 'version=1.2.3' "$fixture_root/output" \
  || fail "a branch rehearsal did not retain its requested version"
grep -Fxq 'publish_authorized=false' "$fixture_root/output" \
  || fail "a branch rehearsal was authorized to publish"
expect_failure "invalid rehearsal version" \
  invoke workflow_dispatch refs/heads/main branch main 1.2

invoke workflow_dispatch refs/heads/main branch main '' >/dev/null
grep -Fxq 'version=0.0.0-dev.42' "$fixture_root/output" \
  || fail "an unversioned branch rehearsal did not use its run-scoped development version"

printf 'release workflow policy test passed\n'
