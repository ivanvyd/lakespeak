#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf '::error::%s\n' "$1" >&2
  exit 1
}

event_name="${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}"
ref="${GITHUB_REF:?GITHUB_REF is required}"
ref_type="${GITHUB_REF_TYPE:?GITHUB_REF_TYPE is required}"
ref_name="${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
run_number="${GITHUB_RUN_NUMBER:?GITHUB_RUN_NUMBER is required}"
output_file="${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
requested_version="${INPUT_VERSION:-}"

case "$event_name" in
  push)
    publish_requested=true
    ;;
  workflow_dispatch)
    publish_requested=false
    ;;
  *)
    fail "Unsupported release event: $event_name."
    ;;
esac

semver='(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?'
is_release_ref=false
if [[ "$ref_type" == 'tag' && "$ref" == "refs/tags/$ref_name" && "$ref_name" =~ ^v${semver}$ ]]; then
  is_release_ref=true
fi

if [[ "$is_release_ref" == 'true' ]]; then
  release_tag="$ref_name"
  version="${release_tag#v}"

  if [[ -n "$requested_version" && "$requested_version" != "$version" ]]; then
    fail "Requested version $requested_version does not match release tag $release_tag."
  fi

  # Fetch the exact ref rather than trusting the checkout's abbreviated tag state. A
  # lightweight tag reaches verify-tag and fails there because it has no signature.
  git fetch --no-tags origin "refs/tags/${release_tag}:refs/tags/${release_tag}"
  tag_commit="$(git rev-parse "${release_tag}^{commit}")"
  checkout_commit="$(git rev-parse HEAD)"
  if [[ "$tag_commit" != "$checkout_commit" ]]; then
    fail "Tag $release_tag resolves to $tag_commit, but the workflow checked out $checkout_commit."
  fi

  # A tag at an unmerged commit could carry its own allowed_signers file and authorize itself.
  # Anchor both ancestry and the signer list in the current canonical release branch instead.
  git fetch --no-tags origin refs/heads/main
  release_branch_commit="$(git rev-parse FETCH_HEAD)"
  if ! git merge-base --is-ancestor "$tag_commit" "$release_branch_commit"; then
    fail "Tag $release_tag is not contained in origin/main."
  fi

  trusted_signers="$(mktemp)"
  trap 'rm -f "$trusted_signers"' EXIT
  if ! git show "${release_branch_commit}:.github/allowed_signers" > "$trusted_signers"; then
    fail "origin/main does not contain .github/allowed_signers."
  fi
  if ! git -c gpg.ssh.allowedSignersFile="$trusted_signers" verify-tag "$release_tag"; then
    fail "Tag $release_tag is not signed by a key allowed on origin/main. See RELEASING.md."
  fi

  publish_authorized="$publish_requested"
elif [[ "$publish_requested" == 'true' ]]; then
  fail "Publishing requires an exact signed v* tag; a branch push is refused."
else
  release_tag=''
  publish_authorized=false
  if [[ -n "$requested_version" ]]; then
    if [[ ! "$requested_version" =~ ^${semver}$ ]]; then
      fail "Rehearsal version must be a valid SemVer value without a leading v."
    fi
    version="$requested_version"
  else
    [[ "$run_number" =~ ^[0-9]+$ ]] || fail "GITHUB_RUN_NUMBER must be numeric."
    version="0.0.0-dev.${run_number}"
  fi
fi

{
  printf 'version=%s\n' "$version"
  printf 'release_tag=%s\n' "$release_tag"
  printf 'publish_authorized=%s\n' "$publish_authorized"
} >> "$output_file"

printf 'building %s\n' "$version"
if [[ "$publish_authorized" == 'true' ]]; then
  printf 'publishing is authorized by trusted tag %s at %s\n' "$release_tag" "$checkout_commit"
fi
