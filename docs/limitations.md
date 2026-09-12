# Limitations

What to know before relying on this.

## It cannot make an answer correct

Genie turns a natural-language question into SQL. That SQL can be wrong in ways that read as
entirely plausible: a subtly wrong join, a date boundary off by one, a filter that quietly excludes
cancelled orders. The answer is still a confident sentence with a number in it.

LakeSpeak preserves the generated SQL, the query result and the message identifiers precisely so a
person can check. It has no way to judge correctness, and neither does any other client.

## Genie may answer with a question

Genie sometimes responds by asking for clarification rather than answering — for example, "Would
you prefer to see the active customers for exactly the quarter '2026-Q1' instead of any quarter
containing '2026-Q1'?" This is observed behaviour, not a hypothetical; it happened on the second
question of the first Question Pack ever run against a live workspace.

The message still completes successfully and the clarification arrives as the answer text, because
that is genuinely what Genie returned. LakeSpeak does not try to detect it: distinguishing "a
question back" from "an answer phrased as a question" is a judgement call, and guessing wrong in
either direction is worse than reporting faithfully.

The consequence for automation is worth planning around. An unattended Question Pack can produce a
report whose answer is a request for clarification, and it will exit `0` because nothing failed.
Phrase pack questions to be unambiguous, and read reports rather than trusting the exit code alone.

## It cannot see more than you can

Every request carries the selected caller identity, and Unity Catalog decides what that identity
sees. Interactive use can run as a user; unattended use can run as a service principal through
OAuth M2M. LakeSpeak cannot impersonate another identity or widen either identity's access. If the
caller can see more than expected, that is a workspace governance question rather than a LakeSpeak
one.

## Conversation state lives in Databricks

There is no local conversation database. History therefore survives across machines, and is also
subject to whatever retention Databricks applies. LakeSpeak stores only a pointer: profile, Agent
id, conversation id.

## LakeSpeak remains pre-1.0

The Genie Conversation API became [generally available on
2026-04-02](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026#april-2-2026), but LakeSpeak's
own public API has not reached 1.0. Service fields and statuses can still evolve. The client maps an
unrecognised status to `Unknown` and treats it as non-terminal rather than throwing, but a larger
contract change can still break it. [`planning/genie-api-surface.md`](planning/genie-api-surface.md)
records which paths have evidence and which do not.

## Authentication depends on the workload

Interactive use brokers OAuth tokens through the Databricks CLI. For unattended use, set
`DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET`; LakeSpeak's native OAuth M2M provider
acquires and refreshes short-lived access tokens in memory. `DATABRICKS_TOKEN` remains available
for local debugging, but a personal access token is a standing credential with no automatic
refresh and is not the recommended scheduled-job path.

## Exports are yours to look after

An exported CSV is an ordinary file containing governed data. LakeSpeak warns before writing and
refuses to overwrite without confirmation; it cannot protect the file afterwards. In CI, remember
that job logs are usually readable by everyone with repository access.

## Large results are assembled, and a shortfall is always flagged

The Statement Execution contract splits large results into chunks. The client follows
`next_chunk_internal_link` when Genie supplies it. If Genie advertises `next_chunk_index` but
omits the link, the client constructs the documented workspace-relative chunk endpoint from the
statement id and next index. See
[ADR 0004](decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md).

When it cannot complete one, it says so. A successor with neither a link nor a statement id, a link
that resolves outside the workspace, a chunk the caller is not permitted to read, or hitting
`MaxResultRows` all produce a result reported as **truncated** — in the terminal, in the JSON
`truncated` field, and in Question Pack reports. An export can be incomplete; it is never *silently*
incomplete.

Two caveats worth knowing. `MaxResultRows` defaults to 100,000 rows, because following a chunked
result to its end is otherwise unbounded work held in memory — raise it if you would rather have
the memory cost than the shortfall. The complete composition was verified against the live AWS
workspace on 2026-09-01: a wide 1,000-row result spanned four chunks, Genie omitted the next link,
and LakeSpeak returned every row without truncation. See [compatibility.md](compatibility.md).

The truncation flag itself was wrong until a post-ship review caught it: the client relied on
`manifest.truncated`, which reports statement-level truncation by Databricks and is `false` for a
merely-chunked result. A large result was returned as its first chunk labelled complete.

## Not implemented

Visualization rendering, conversation list and resume commands, and an MCP server mode. See
[ROADMAP.md](../ROADMAP.md) for what is planned and [GOVERNANCE.md](../GOVERNANCE.md) for what is
deliberately out of scope.
