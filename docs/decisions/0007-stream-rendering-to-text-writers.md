# 0007 — Stream rendering to `TextWriter`

**Status:** Accepted
**Date:** 2026-09-12

## Context

The public rendering helpers returned a complete `string`. The Genie client already holds fetched
rows in memory, so building another document-sized string made large JSONL, CSV, and Markdown
outputs pay an avoidable second memory cost. The CLI then copied that string to stdout or a file.
Question Pack reports had the same shape after retaining every response until the run finished.

The existing string methods are convenient and are already public. Removing or changing them would
break callers. A full incremental result API would also change the client contract and needs its own
design for truncation, cancellation, and HTTP chunk ownership.

## Decision

Add synchronous and asynchronous `TextWriter` overloads for JSONL, CSV, and Markdown, and retain the
existing string-returning methods as compatibility wrappers. Writers encode one row at a time and
observe cancellation between rows on asynchronous paths.

JSONL validates its object shape before emitting any bytes. Duplicate column names and rows wider
than their declared schema fail explicitly because JSON objects cannot preserve them losslessly.
Short rows omit properties for cells that Databricks did not return. CSV encodes SQL `NULL` as an
unquoted empty field and a literal empty string as a quoted empty field.

The CLI uses the writer paths. Question Pack execution writes each completed section directly to a
disk-backed spool, retains compact outcome summaries for the final header, and then streams the
header and sections to stdout or an atomically installed destination. The existing public
`PackRunner.RunAsync` and `PackReportWriter.WriteMarkdown` APIs remain available.

## Consequences

Large encoded outputs no longer require a second document-sized managed string on the CLI path.
Callers that need an in-memory string retain the original convenience API and its corresponding
allocation. The client still materializes the fetched result rows; this decision does not claim a
bounded end-to-end result footprint.

The new public overloads are additive API surface. Their eventual package version belongs to the
release scope decision; this ADR does not select or publish a version.

Question Pack staging is disk-backed rather than size-bounded: while installing a file it can use a
spool plus a same-directory staging file, for a temporary peak near twice the encoded report size.
That cost buys deterministic headers, cancellation safety, and atomic replacement without retaining
all response graphs or one rendered section in managed memory.
