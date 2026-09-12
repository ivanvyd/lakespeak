# Command reference

Every command accepts the global options below. `lakespeak <command> --help` prints the same
information at the terminal.

## Global options

| Option | Meaning |
|---|---|
| `--profile`, `-p` | Databricks CLI profile to authenticate with |
| `--format`, `-f` | `text` (default), `table`, `markdown`, `json`, `jsonl`, `csv` |
| `--quiet`, `-q` | Suppress progress output on stderr |
| `--verbose` | Print diagnostics to stderr. Credentials are always redacted |

Results go to **stdout**; progress, warnings and errors go to **stderr**. That split is what lets
`lakespeak ask --format json … 2>/dev/null` produce clean JSON for a script.

ANSI decoration is suppressed when output is redirected, when `NO_COLOR` is set, or when the
format is machine-readable. `NO_COLOR` does not make a real terminal noninteractive: `chat` still
prompts and progress remains plain text. Use `--quiet` to suppress progress; machine-readable
formats suppress it automatically.

## `lakespeak agents list`

Lists the Genie Agents your identity can see.

```bash
lakespeak agents list
lakespeak agents list --format json
```

An identity with no Genie grants sees nothing. That is reported as a warning with exit `0`, not as
an error — the fix is a Databricks permission, not a change to how you invoked the command. Machine
output remains parseable: JSON emits `[]`, CSV emits its header, and JSONL emits zero rows; the
explanation stays on stderr.

## `lakespeak ask <question>`

Asks a question in a new conversation and prints the answer.

```bash
lakespeak ask --agent sales "Who were our five fastest-growing customers?"
lakespeak ask --agent finance --format json "What was recognized revenue yesterday?"
lakespeak ask --agent finance --show-sql "Revenue by market and month"
```

| Option | Meaning |
|---|---|
| `--agent`, `-a` | Agent id, exact title, or a configured alias |
| `--show-sql` | Print the generated SQL alongside the answer |

`--agent` accepts an id, a title, or an alias from your config file. If a name matches more than
one Agent, the command **fails** rather than picking one — answering against the wrong Finance
Agent looks exactly like success.

With `--format csv`, the *query result* is written. A narrative answer has no rows, so if the
response carries no result you get a warning on stderr and nothing on stdout.

When a returned result is incomplete, every output format emits a warning on stderr without
contaminating machine-readable stdout. JSONL also refuses duplicate column names or a row wider
than its schema, because a JSON object cannot preserve either shape without losing data; use JSON
or CSV for those results.

CSV writes SQL `NULL` as an unquoted empty field and a literal empty string as `""`. To prevent
spreadsheet formula execution, a value beginning with `=`, `+`, `-`, or `@` is visibly prefixed
with a single quote.

## `lakespeak chat`

An interactive, stateful conversation. Follow-up questions keep their context.

```bash
lakespeak chat
lakespeak chat --agent platform-operations
```

With no `--agent`, a selector appears so you never have to paste an id.

Requires an interactive terminal — it refuses to run against a pipe, because a chat loop reading
EOF spins and a selector nobody can answer looks like a hang. Use `ask` for scripts.

Ctrl+C cancels the question in flight and returns you to the prompt; it does not end the session.

| Slash command | Meaning |
|---|---|
| `/help` | List commands |
| `/agents` | List available Agents |
| `/use <agent>` | Switch Agent and start a new conversation |
| `/new` | Start a new conversation with the same Agent |
| `/sql` | Show the SQL behind the last answer |
| `/result` | Show the last query result |
| `/export <path>` | Write the last result to CSV |
| `/thumbs-up`, `/thumbs-down [comment]` | Send feedback to Databricks |
| `/exit` | Leave (`/quit` does the same) |

## `lakespeak pack`

```bash
lakespeak pack init my-brief.yaml       # write a starter pack
lakespeak pack validate my-brief.yaml   # check it without running it
lakespeak pack run my-brief.yaml        # run it and write the report
```

| Option (on `run`) | Meaning |
|---|---|
| `--output`, `-o` | Write the report here instead of the pack's configured path |
| `--force` | Overwrite an existing report |

`validate` reports **every** problem at once rather than stopping at the first, so fixing a pack is
one pass instead of repeated guessing.

See the [Question Pack guide](question-packs.md).

## `lakespeak export [last]`

Exports the last answer's query result without opening a chat session — the scriptable
counterpart to chat's `/export`.

```bash
lakespeak export last --output revenue.csv
lakespeak export last                       # to stdout
```

| Option | Meaning |
|---|---|
| `--output`, `-o` | File to write. Defaults to stdout |
| `--force` | Overwrite the output file if it exists |

The result is **re-fetched from Databricks** rather than cached locally: a local copy of governed
data would have none of the governance, and Databricks is already the system of record.

If the cached result has expired, this command re-runs the query rather than failing. That costs
warehouse time, which is the right trade here because you explicitly asked to export the rows —
`ask` deliberately does not re-run on your behalf.

If the result is incomplete — truncated by Databricks, or continuing beyond the rows this version
reads — the command says so on stderr. It never writes a partial export silently.
File exports are staged beside the destination and installed atomically. Cancellation or a write
failure leaves an existing complete CSV unchanged; without `--force`, a concurrently created file
also wins rather than being overwritten.

## `lakespeak feedback [last] --rating <r>`

Rates the last answer without opening a chat session.

```bash
lakespeak feedback last --rating negative --comment "Included cancelled orders."
lakespeak feedback last --rating positive
```

| Option | Meaning |
|---|---|
| `--rating`, `-r` | `positive`, `negative`, or `none`. Required |
| `--comment`, `-c` | Free-text comment sent to Databricks |

Databricks rejects a comment alongside a `none` rating. LakeSpeak refuses that combination before
sending, exiting `2`, rather than surfacing an HTTP 400 that reads like a transport fault.

Both commands read a pointer file written by the last `ask`. It records identifiers only — Agent,
conversation, message and attachment ids — plus the effective profile and normalized workspace
origin used for routing. It never records a question, an answer, a token, or a row.

## `lakespeak auth check`

Verifies that a profile resolves, that a token can be obtained, and that the workspace answers.

```bash
lakespeak auth check --profile company
```

Prints the token's length, never any part of its value. Warns about profiles holding a legacy
personal access token.

## `lakespeak config show`

Prints the effective configuration and, for each value, where it came from.

```bash
lakespeak config show
```

The output contains no credentials, so it is safe to paste into an issue.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Unexpected failure |
| 2 | Invalid command, configuration, or Question Pack |
| 3 | Authentication failure |
| 4 | Authorization failure |
| 5 | Agent or conversation not found |
| 6 | Genie could not answer |
| 7 | Timeout or cancellation |
| 8 | Question Pack finished with some questions failed |
| 9 | Unsupported or malformed response |

These are contractual. An existing code will not change meaning.
