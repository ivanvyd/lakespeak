# Genie Conversation API surface

Every field name and path below was read from the generated Databricks SDKs
(`databricks-sdk-py@main`, corroborated against `databricks-sdk-go`), which are the most reliable
published record of the wire contract. Prose documentation was not treated as authoritative where
the two could disagree.

One caveat on that corroboration: the Python, Go and Java SDKs are all generated from a single
internal specification. Their agreement is one source in three forms, not three independent
confirmations. It is still a far better source than the prose docs, which contain at least one
example that would have broken this client (see Contradictions).

Anything marked UNVERIFIED was not confirmed from that source. The client tolerates those rather
than hard-coding them.

## Terminology

The public product name is "Genie Agent". **The API is entirely `space`-based**: every path segment
is `spaces`, and every identifier is `space_id`. There is no `agents` path.

`LakeSpeak.Genie` therefore exposes `GenieAgent` / `AgentId` in its public surface and keeps
`space_id` in the wire DTOs. The mapping happens once, at the serialization boundary.

## Endpoints

All under `/api/2.0/genie`. VERIFIED unless noted.

| Operation | Verb | Path |
|---|---|---|
| List spaces | GET | `/spaces` |
| Get space | GET | `/spaces/{space_id}` |
| Start conversation | POST | `/spaces/{space_id}/start-conversation` |
| Create follow-up message | POST | `/spaces/{space_id}/conversations/{conversation_id}/messages` |
| Get message | GET | `/spaces/{space_id}/conversations/{conversation_id}/messages/{message_id}` |
| Get attachment query result | GET | `…/messages/{message_id}/attachments/{attachment_id}/query-result` |
| Re-execute attachment query | POST | `…/attachments/{attachment_id}/execute-query` |
| Generate full-result download | POST | `…/attachments/{attachment_id}/downloads` |
| Get full-result download | GET | `…/attachments/{attachment_id}/downloads/{download_id}` |

The download pair is **not used**, and the row above understates what it needs. `generate` returns
`{ download_id, download_id_signature }` — the signature being a JWT — and `get` requires **both**,
not the id alone. Its response is a `statement_response`, which is chunked like any other, so it
does not by itself return a whole result. Verified against `databricks-sdk-py` and the CLI
reference for `databricks genie get-download-full-query-result`, which takes six positional
arguments ending in `DOWNLOAD_ID_SIGNATURE`.

## Completing a chunked result

There is no Genie endpoint that takes a chunk index. Genie returns a nested Statement Execution
response, but it does not always preserve every result field. A live 2026-09-01 response retained
the statement id and `next_chunk_index` while omitting `next_chunk_internal_link`; the underlying
SQL Statement response still exposed the next chunk at
`/api/2.0/sql/statements/{statement_id}/result/chunks/{next_chunk_index}`.

The client follows `next_chunk_internal_link` when present. When Genie omits it, the client rejects
dot-only statement ids and escapes every other id into the documented workspace-relative SQL
Statement chunk endpoint. Both forms go through the same workspace scheme, host and port validation
before an authenticated request; see
[ADR 0004](../decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md).

The caller's identity can read remaining chunks of a statement Genie executed on its behalf. A
live AWS probe assembled four chunks and all 1,000 rows. Permission denial remains possible under
different workspace grants; the client treats a refusal as a truncated result rather than a
failure.
| Send message feedback | POST | `…/messages/{message_id}/feedback` |
| List conversations | GET | `/spaces/{space_id}/conversations` |
| List conversation messages | GET | `/spaces/{space_id}/conversations/{conversation_id}/messages` |

Not used by v0.1, and listed so nobody re-derives them: `create_space`, `update_space`,
`trash_space`, `delete_conversation`, the `eval-runs/*` family, and the comment endpoints. Creating
or optimising Agents is out of scope per `GOVERNANCE.md`.

`download_message_attachment_visualization` is `GET /api/2.0/genie/{name}/download-visualization`.
The `{name}` segment does not follow the pattern of any other endpoint. **UNVERIFIED** — not used
in v0.1.

## Message status

Ten values, VERIFIED against the SDK enum:

```
SUBMITTED  FILTERING_CONTEXT  FETCHING_METADATA  ASKING_AI  PENDING_WAREHOUSE
EXECUTING_QUERY  COMPLETED  FAILED  CANCELLED  QUERY_RESULT_EXPIRED
```

Terminal: `COMPLETED`, `FAILED`, `CANCELLED`. Polling stops on these.

**Spelling trap.** Genie spells it `CANCELLED`, with two Ls. The SQL Statement Execution API
spells its own `StatementState.CANCELED` with one. The two must never share a parser; a test
asserts the SQL spelling falls through to `Unknown` here rather than quietly ending a poll.

`QUERY_RESULT_EXPIRED` is **not** a failure. The message succeeded; its cached SQL result aged out.
Recovery is `execute-query` on the attachment, not re-asking the question.

Databricks documents the set as open-ended, so the client maps to a closed internal enum with an
`Unknown` arm rather than switching exhaustively over platform states. A status added by Databricks
next month must not crash a released client.

## Response shapes

### GenieMessage

Required: `space_id`, `conversation_id`, `message_id`, `content`.
Optional: `attachments[]`, `status`, `error`, `feedback`, `query_result`, `created_timestamp`,
`last_updated_timestamp`, `user_id`, `id`.

`content` is the **question that was asked**, not the answer. The answer arrives in
`attachments[].text.content`. Reading `content` as the answer is the single easiest mistake to make
against this API.

### GenieAttachment

`attachment_id`, plus exactly one populated variant: `text`, `query`, `suggested_questions`, `viz`.

`suggested_questions` is a fourth variant that most descriptions of this API omit.

### GenieQueryAttachment

`query` (**the generated SQL — the field is `query`, not `sql`**), `title`, `description`,
`statement_id`, `parameters[]`, `query_result_metadata`, `thoughts[]`, `id`,
`last_updated_timestamp`.

### TextAttachment

`content`, `id`, `phase` (`RESPONSE_PHASE_THINKING` | `RESPONSE_PHASE_VERIFYING`),
`purpose` (`FOLLOW_UP_QUESTION`), `verification_metadata` (`index`, `section`).

### Query result

`GenieGetMessageQueryResultResponse` has exactly one field: `statement_response`, of type
`sql.StatementResponse`.

**The Genie query result reuses the SQL Statement Execution API contract**: `manifest` (schema and
column types), `result` (`data_array`, `external_links`, `chunk_index`, `row_count`), `status`.
Anything already known about that API applies here, including that presigned `external_links` URLs
must be fetched **without** the `Authorization` header.

`GenieResultMetadata`: `is_truncated`, `row_count`.

### Others

- `GenieSpace`: `space_id`, `title`, `description`, `warehouse_id`, `etag`, `parent_path`, `serialized_space`
- `GenieListSpacesResponse`: `spaces[]`, `next_page_token`
- `GenieStartConversationResponse`: `conversation_id`, `message_id`, `conversation`, `message`
- `GenieConversation`: `space_id`, `conversation_id`, `title`, timestamps, `user_id`
- `GenieFeedback`: `rating` (`POSITIVE` | `NEGATIVE` | `NONE`), `comment`
- `MessageError`: `error`, `type`
- `QueryAttachmentParameter`: `keyword`, `sql_type`, `value`
- `GenieGenerateDownloadFullQueryResultResponse`: `download_id`, `download_id_signature`

### Secrets in response bodies

Two fields are bearer-equivalent and both are redacted in all diagnostic output:

- `download_id_signature` — the SDK documents it as "JWT signature for the download_id to ensure
  secure access to query results".
- `statement_id_signature` — carried on the message-level `query_result` summary. A different
  field from the one above, and missed by the first version of the scrubber.

The message-level `query_result` is a **summary only** — `is_truncated`, `row_count`,
`statement_id`, `statement_id_signature`. It carries no rows. Rows come only from the
query-result endpoint. This client does not read it.

## Corrections to the original specification

The plan this project was built from proposed a model that does not match the API. These are
corrections, not preferences:

| Plan said | Reality |
|---|---|
| 7 message states (`Submitted`, `Processing`, `ExecutingQuery`, …) | 10 states. `FETCHING_METADATA`, `FILTERING_CONTEXT`, `ASKING_AI`, `PENDING_WAREHOUSE` and `QUERY_RESULT_EXPIRED` were all absent. |
| `GenieQuery.IsTrustedAsset` | **No such field exists** anywhere on the query attachment. Modelling it would have meant inventing a trust signal and showing it to users. Dropped. |
| Attachments are text / query / visualization | There is also `suggested_questions`. |
| Generated SQL in `sql` | The field is `query`. |
| Poll "every 1-5 seconds" | Consistent with the docs; the client defaults to 1s with backoff to 5s. |

## Contradictions between the docs and the SDKs

Trust the SDK in every case below.

| Documentation says | Reality |
|---|---|
| A message status of `IN_PROGRESS` | Appears in no SDK. A client that threw on unrecognised statuses would compile, pass its mocks, and fail against the live service. This is the single strongest argument for the `Unknown` arm. |
| Query result at `…/messages/{id}/query-result/{attachment_id}` | That is the deprecated `GetMessageQueryResultByAttachment`. Use `…/attachments/{attachment_id}/query-result`. The same split exists on execute-query. |
| Reasoning traces live on `GenieQueryAttachments` | No such type. They are at `attachments[].query.thoughts[]`. |
| `update-space` is POST | The SDK generates PATCH. |
| The Conversation API was Public Preview | The official 2026 release notes record [general availability on 2026-04-02](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026#april-2-2026). Visualization retrieval became [generally available on 2026-08-26](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026#august-26-2026), although LakeSpeak still does not use or live-test that path. |

## Response envelope differences

Three send paths, three shapes. Getting this wrong deserialises to an object of nulls rather
than failing loudly:

- `start-conversation` → `{ conversation, conversation_id, message, message_id }`
- create follow-up message → a **bare `GenieMessage`**, unwrapped
- `get-message` → a bare `GenieMessage`
- `…/query-result` → **wrapped**: `{ "statement_response": { … } }`. The bare SQL Statement
  Execution API is unwrapped; Genie wraps it.

Request body for both send paths: `content` (required), plus optional `enable_visualization`.

`message_id` is read as `message_id ?? id`: published examples omit `message_id` even though the
SDK marks it required.

Feedback returns an **empty response body**; nothing should try to parse it.

## Undocumented constraint, found by calling the API

`POST .../feedback` rejects a request carrying both `comment` and `rating: NONE`:

> Feedback text cannot be provided when rating is NONE. Text feedback is only allowed with
> POSITIVE or NEGATIVE ratings.

This appears in no SDK and no documentation page. It was found by a live integration test, and the
client now rejects the combination before the request rather than surfacing an HTTP 400 that reads
like a transport fault.

## Documented limits

10,000 conversations per Agent, 10,000 messages per conversation, 30 tables per Agent.

## Not established

| Item | Status |
|---|---|
| Rate-limit response shape and retry-after semantics | UNVERIFIED. Client treats HTTP 429 as retryable and honours `Retry-After` when present. |
| Error body JSON shape (`error_code` / `message`) | UNVERIFIED for Genie specifically. Client parses defensively and falls back to the raw status. |
| `databricks auth token` emits `{ access_token, token_type, expiry }` | VERIFIED from the CLI reference. **U2M only — M2M is explicitly unsupported by this command**, which is why automation uses environment credentials instead. The `expiry` format is documented only as a placeholder, so it is parsed permissively. |
| `created_timestamp` unit | UNVERIFIED. Typed `int64`, unit undocumented, and the one published example is 10 digits (seconds) while the field is named like milliseconds. Detected by magnitude rather than assumed. |
| Whether `id` and `message_id` can ever differ | UNVERIFIED. Both exist and no documentation explains the relationship. |
| Any numeric API rate limit | UNVERIFIED. A widely-repeated "5 requests/minute" figure does not appear on either candidate Databricks page, and Genie is absent from the per-endpoint RPS table. No number is hard-coded; HTTP 429 is backed off generically. |
| `parameters` on a query attachment implying a trusted asset | UNVERIFIED inference, deliberately **not** modelled. It is the kind of signal that would be shown to a user as if Databricks had asserted it. |
| Behaviour against AWS and GCP workspaces | Not tested. Paths are identical, which is not evidence. |
