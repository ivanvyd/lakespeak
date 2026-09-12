# Contributing

Thanks for considering it. This is a small, deliberately narrow project — read
[GOVERNANCE.md](GOVERNANCE.md) first if you are proposing a feature, because scope is the most
likely reason for a pull request to be declined, and that is easier to hear before you write the
code than after.

## Getting set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for anything touching
authentication, the [Databricks CLI](https://docs.databricks.com/dev-tools/cli/install.html).

```bash
git clone https://github.com/ivanvyd/LakeSpeak.NET.git
cd LakeSpeak.NET
dotnet restore
dotnet build -c Release
dotnet test -c Release --filter-not-trait "Category=Live" --ignore-exit-code 8
```

The default test run needs no Databricks workspace and no credentials. That is deliberate: a
contributor should be able to make a change and prove it without an account or a bill.

## How the solution is laid out

Two projects ship to NuGet. The rest are internal to the CLI tool and are bundled into it, which
is why they are not separate packages — every extra public package is an API to support forever.

| Project | Ships? | What it is |
|---|---|---|
| `LakeSpeak.Genie` | **NuGet** | The client: wire contracts, polling, attachments, auth, typed failures |
| `LakeSpeak.Cli` | **NuGet** (dotnet tool) | Commands, console output, the `lakespeak` entry point |
| `LakeSpeak.Configuration` | internal | The config file, agent aliases, the last-answer pointer |
| `LakeSpeak.Application` | internal | Agent resolution — turning what you typed into one Agent |
| `LakeSpeak.Rendering` | internal | Terminal tables, CSV, Markdown, JSON, control-character safety |
| `LakeSpeak.QuestionPacks` | internal | Pack schema, validation, runner, report writer |

The wire/domain split matters: everything in `LakeSpeak.Genie/Wire/` is `internal` and mirrors the
Databricks response shapes exactly, including `space_id`. The public surface uses Agent
terminology. That translation happens once, at the serialization boundary — see
[`docs/planning/genie-api-surface.md`](docs/planning/genie-api-surface.md).

## Before opening a pull request

```bash
dotnet build -c Release            # warnings are errors
dotnet format --verify-no-changes
dotnet test -c Release --filter-not-trait "Category=Live" --ignore-exit-code 8
```

If you added or changed a dependency, regenerate the lock files and commit them:

```bash
dotnet restore --force-evaluate
```

CI restores in `--locked-mode`, so a stale lock file fails the build rather than silently
resolving to different versions on the runner than on your machine.

## What a good change looks like

**A test that would fail without the fix.** For anything that could regress, prove it: revert your
fix, watch the test go red, restore it, watch it go green. A test that passes either way is a
comment shaped like a test.

**Wire changes backed by evidence.** If you change how a Databricks response is parsed, say where
the field name came from. The generated SDKs are a more reliable source than the prose docs, which
contain at least one status value that does not exist.
[`docs/planning/genie-api-surface.md`](docs/planning/genie-api-surface.md) records which claims are
verified and which are not — add to that table rather than quietly widening a parser.

**Comments that say why, not what.** This codebase leans on comments explaining the failure a piece
of code prevents. Restating the code in English is noise, and gets removed.

**An ADR for anything architectural.** Public API or architecture changes need one in
`docs/decisions/`, in the same pull request, in the existing format.

## Live tests

Tests marked `Category=Live` need a real workspace with a Genie Agent and cost real compute. They
never run for pull requests from forks, because they require credentials. Run them yourself with:

```bash
dotnet test -c Release --filter-trait "Category=Live" --ignore-exit-code 8
```

Do not point them at a production workspace.

## Reporting a bug

Include what you ran, what happened, and what you expected. `lakespeak --verbose` prints
diagnostics with credentials redacted.

**Read the output before pasting it.** Questions, answers and query results can contain your
organisation's data. LakeSpeak redacts credentials; it has no way to know which of your table names
are sensitive.

For security issues, do not open an issue — see [SECURITY.md](SECURITY.md).
