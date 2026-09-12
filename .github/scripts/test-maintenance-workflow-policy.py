#!/usr/bin/env python3
"""Static invariants for the write-capable lock-refresh workflow."""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CI = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
REFRESH = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")


def require(text: str, fragment: str, message: str) -> int:
    position = text.find(fragment)
    if position < 0:
        raise AssertionError(message)
    return position


def forbid(text: str, fragment: str, message: str) -> None:
    if fragment in text:
        raise AssertionError(message)


def main() -> None:
    require(CI, "  workflow_dispatch:\n", "CI must accept the post-refresh dispatch")

    validate = require(
        REFRESH,
        "      - name: Validate the maintenance branch\n",
        "the branch must be validated",
    )
    checkout = require(
        REFRESH,
        "      - uses: actions/checkout@",
        "the refresh workflow must check out the requested branch",
    )
    restore = require(
        REFRESH,
        "      - run: dotnet restore --force-evaluate\n",
        "the refresh workflow must regenerate lock files",
    )
    build = require(
        REFRESH,
        "      - run: dotnet build --no-restore -c Release\n",
        "the regenerated graph must build before it is written",
    )
    credentials = require(
        REFRESH,
        "          GH_TOKEN: ${{ github.token }}\n",
        "the bounded write step must receive the repository token",
    )
    push = require(
        REFRESH,
        '          git push origin "HEAD:refs/heads/$BRANCH"\n',
        "the verified lock files must be pushed to the same maintenance branch",
    )
    dispatch = require(
        REFRESH,
        '        run: gh workflow run ci.yml --ref "$BRANCH"\n',
        "CI must be dispatched for the new commit",
    )

    if not validate < checkout < restore < build < credentials < push < dispatch:
        raise AssertionError(
            "branch validation, unprivileged checks, bounded credentials, push, and CI dispatch "
            "must remain in that order"
        )

    require(
        REFRESH,
        "          persist-credentials: false\n",
        "checkout must not expose a write-capable token to restore or build",
    )
    require(
        REFRESH,
        "            dependabot/nuget/*) ;;\n",
        "only NuGet Dependabot branches may be refreshed",
    )
    require(
        REFRESH,
        "          git add -- '*packages.lock.json'\n",
        "the write step must stage only lock files",
    )
    require(REFRESH, "      actions: write\n", "dispatching CI requires actions: write")
    require(REFRESH, "          gh auth setup-git\n", "the bounded push step must configure GitHub auth")

    forbid(REFRESH, "persist-credentials: true", "checkout credentials must stay disabled")
    forbid(REFRESH, "\n          git add -A", "the workflow must not stage unrelated files")
    forbid(REFRESH, "secrets.PAT", "the workflow must not introduce a long-lived PAT")

    print("maintenance workflow policy test passed")


if __name__ == "__main__":
    main()
