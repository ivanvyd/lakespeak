# Question Packs

A Question Pack turns a set of business questions into a repeatable, reviewable report. It lives in
your repository, goes through code review like anything else, and produces the same structure every
time it runs.

```bash
lakespeak pack init daily-brief.yaml
lakespeak pack validate daily-brief.yaml
lakespeak pack run daily-brief.yaml
```

## A pack

```yaml
apiVersion: lakespeak.net/v1alpha1
kind: QuestionPack

metadata:
  name: daily-platform-brief
  description: Daily summary of Databricks platform health

spec:
  agent: platform-operations
  profile: automation-workspace

  questions:
    - id: failed-jobs
      title: Failed production jobs
      ask: >
        Which production jobs failed during the last 24 hours?
        Include job name, failure time, and latest error category.
      timeout: 90s

  output:
    format: markdown
    path: reports/daily-platform-brief.md

  behavior:
    continueOnQuestionFailure: true
    includeGeneratedSql: false
    includeTimings: true
    includeIdentifiers: false
```

The full schema is published at
[`schemas/question-pack-v1alpha1.schema.json`](../schemas/question-pack-v1alpha1.schema.json).

## Fields worth understanding

**`spec.agent` is required and never inferred.** A report that silently ran against a different
Agent is worse than one that failed.

**`spec.profile` is optional and cannot be blank.** When present it selects the Databricks profile
for this pack. An explicit `--profile` still wins; otherwise the pack profile wins over an Agent
alias profile and `defaults.profile`.

**`behavior.continueOnQuestionFailure`** (default `true`) finishes the run and records failures in
place, exiting `8`. Set it to `false` to stop at the first failure. For a scheduled report, partial
results usually beat none.

**`behavior.includeIdentifiers`** (default `false`) adds conversation and message ids so an answer
can be traced back in Databricks. It is off by default because those ids identify a conversation
containing governed data, and reports get committed.

**`behavior.timeout`** (default `10m`) is the wait for questions without their own timeout. A
question's positive `timeout` value overrides it. The timer limit is about 24.9 days (`596h` when
written in whole hours). Invalid, zero, overflowing, and longer durations are reported together
with the pack's other validation errors.

**`spec.output.path`** is relative to the pack file. Absolute paths, paths outside the pack
directory, and paths passing through a symbolic link or junction are rejected — a pack can arrive
in a pull request, so its output path is attacker-influenced. The destination is prepared before
any warehouse call. LakeSpeak stages a complete report next to the destination and installs it
atomically, so a failed or cancelled write does not truncate an existing report. An explicit
`--output` may name another directory, but receives the same link and atomic-write checks.

## How a pack runs

Questions run **sequentially**, and each gets a **fresh conversation**.

Sequentially because each question occupies a SQL warehouse, and ten concurrent questions degrade
a shared warehouse for everyone on it. Reports are not latency-sensitive.

Fresh conversations because Genie is stateful: reusing one would let the answer to question three
depend on questions one and two, making the report order-dependent in a way no reader would
suspect.

## Guarantees

- **A pack is data, never code.** It cannot execute a command, read a file, or widen the
  permissions of the identity running it.
- **Validation is strict.** Unknown keys fail rather than being ignored, so a typo in
  `continueOnQuestionFailure` cannot silently leave the intended behaviour off.
- **All errors at once.** `validate` reports every problem in one pass.
- **No prompts.** A pack run never asks a question interactively, so it cannot hang a cron job.
  An ambiguous Agent name fails instead.
- **Deterministic output.** The same pack against the same data produces a byte-identical report
  apart from the timestamp and timings, which is what makes a committed report reviewable in a
  diff.
- **Capped at 50 questions.** Beyond that it is a scheduled job, not a report.

Each question id becomes an explicit stable report anchor. When generated SQL is included, the
report also lists the parameter values Genie bound to it; SQL without those values is not a full
record of what ran.

## Reports carry a warning

Every generated report states that answers come from natural-language questions and can be wrong in
ways that read as plausible, and tells the reader to check the generated SQL before acting on
anything consequential. That line is not configurable.

## Something to plan around

Genie sometimes replies with a **clarifying question** instead of an answer. The message completes
successfully and the clarification becomes the answer text, so the pack exits `0` with a report
whose answer is a question. Phrase pack questions unambiguously, and read reports rather than
trusting the exit code alone. See [limitations](limitations.md).

## In CI

```yaml
- name: Daily brief
  env:
    DATABRICKS_HOST: ${{ secrets.DATABRICKS_HOST }}
    DATABRICKS_CLIENT_ID: ${{ secrets.DATABRICKS_CLIENT_ID }}
    DATABRICKS_CLIENT_SECRET: ${{ secrets.DATABRICKS_CLIENT_SECRET }}
  run: |
    dotnet tool install --global LakeSpeak.Cli
    lakespeak pack run packs/daily-brief.yaml --force
```

LakeSpeak acquires and refreshes the short-lived OAuth access token in memory. Store the service
principal client id and secret, not an access token that will expire before a later scheduled run.

Remember that job logs are usually readable by everyone with repository access, and a report
contains governed data. Treat the artifact accordingly.
