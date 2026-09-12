using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LakeSpeak.Genie;

namespace LakeSpeak.Rendering;

/// <summary>
/// Serializes a Genie response for another program.
/// </summary>
/// <remarks>
/// The shape is versioned by <see cref="SchemaVersion"/> and is part of the tool's contract:
/// scripts and coding agents parse it. Adding a field is a minor change; renaming or removing
/// one is breaking and needs the version bumped.
/// </remarks>
public static class MachineOutput
{
    public const string SchemaVersion = "1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false,
    };

    private static readonly JavaScriptEncoder RowJsonEncoder =
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static string ToJson(GenieResponse response, string? agentTitle = null, bool indented = true) =>
        JsonSerializer.Serialize(
            Envelope.From(response, agentTitle),
            indented ? Options : CompactOptions);

    /// <summary>
    /// One JSON object per result row, for streaming into another tool.
    /// </summary>
    /// <remarks>
    /// The answer is not repeated on every row. When there is no query result there is nothing
    /// row-shaped to emit, so a single metadata object is written instead of nothing at all —
    /// a consumer reading zero lines cannot tell success from failure.
    /// </remarks>
    public static string ToJsonLines(GenieResponse response, string? agentTitle = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteJsonLines(writer, response, agentTitle);
        return writer.ToString();
    }

    /// <summary>Writes JSONL incrementally, retaining at most one encoded row at a time.</summary>
    /// <remarks>
    /// JSON objects cannot represent duplicate property names losslessly. Duplicate columns and
    /// rows wider than their schema are therefore rejected before anything is written. Short rows
    /// remain representable: properties for cells that were not returned are omitted.
    /// </remarks>
    public static void WriteJsonLines(
        TextWriter writer,
        GenieResponse response,
        string? agentTitle = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        if (response.Result is null)
        {
            writer.WriteLine(JsonSerializer.Serialize(
                Envelope.From(response, agentTitle), CompactOptions));
            return;
        }

        ValidateRowObjectShape(response.Result);
        var rowBuffer = new StringBuilder();
        using var rowWriter = new StringWriter(rowBuffer, CultureInfo.InvariantCulture);
        foreach (var row in response.Result.Rows)
        {
            BuildRow(rowBuffer, rowWriter, response.Result.Columns, row);
            WriteLine(writer, rowBuffer);
        }
    }

    /// <summary>Writes JSONL asynchronously, retaining at most one encoded row at a time.</summary>
    public static async Task WriteJsonLinesAsync(
        TextWriter writer,
        GenieResponse response,
        string? agentTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        if (response.Result is null)
        {
            var envelope = JsonSerializer.Serialize(Envelope.From(response, agentTitle), CompactOptions);
            await writer.WriteLineAsync(envelope.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        ValidateRowObjectShape(response.Result);
        var rowBuffer = new StringBuilder();
        using var rowWriter = new StringWriter(rowBuffer, CultureInfo.InvariantCulture);
        foreach (var row in response.Result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildRow(rowBuffer, rowWriter, response.Result.Columns, row);
            foreach (var chunk in rowBuffer.GetChunks())
            {
                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateRowObjectShape(GenieQueryResult result)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in result.Columns)
        {
            if (!names.Add(column.Name))
            {
                var displayName = column.Name.Length == 0 ? "<empty>" : column.Name;
                throw new InvalidOperationException(
                    $"JSONL cannot represent the duplicate column name '{displayName}' without losing a value. " +
                    "Use JSON or CSV for this result.");
            }
        }

        for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
        {
            var cellCount = result.Rows[rowIndex].Count;
            if (cellCount > result.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"JSONL cannot represent row {rowIndex + 1}: it has {cellCount} cells but the schema " +
                    $"declares {result.Columns.Count} columns. Use JSON or CSV for this result.");
            }
        }
    }

    private static void BuildRow(
        StringBuilder buffer,
        TextWriter writer,
        IReadOnlyList<GenieColumn> columns,
        IReadOnlyList<string?> row)
    {
        buffer.Clear();
        writer.Write('{');
        for (var index = 0; index < row.Count; index++)
        {
            if (index > 0)
            {
                writer.Write(',');
            }

            writer.Write('"');
            RowJsonEncoder.Encode(writer, columns[index].Name);
            writer.Write("\":");

            if (row[index] is { } value)
            {
                writer.Write('"');
                RowJsonEncoder.Encode(writer, value);
                writer.Write('"');
            }
            else
            {
                writer.Write("null");
            }
        }

        writer.Write('}');
    }

    private static void WriteLine(TextWriter writer, StringBuilder line)
    {
        foreach (var chunk in line.GetChunks())
        {
            writer.Write(chunk.Span);
        }

        writer.WriteLine();
    }

    private sealed record Envelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; init; } = MachineOutput.SchemaVersion;

        [JsonPropertyName("agent")]
        public required AgentRef Agent { get; init; }

        [JsonPropertyName("conversationId")]
        public required string ConversationId { get; init; }

        [JsonPropertyName("messageId")]
        public required string MessageId { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("answer")]
        public string? Answer { get; init; }

        [JsonPropertyName("query")]
        public QueryRef? Query { get; init; }

        [JsonPropertyName("result")]
        public ResultRef? Result { get; init; }

        [JsonPropertyName("suggestedQuestions")]
        public IReadOnlyList<string>? SuggestedQuestions { get; init; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; init; }

        internal static Envelope From(GenieResponse r, string? agentTitle) => new()
        {
            Agent = new AgentRef { Id = r.AgentId, Title = agentTitle },
            ConversationId = r.ConversationId,
            MessageId = r.MessageId,
            // Lowercase for a stable, script-friendly vocabulary that does not change if the
            // display name of a state changes.
            Status = r.State.ToString().ToLowerInvariant(),
            Answer = r.Text,
            Query = r.Query is null
                ? null
                : new QueryRef
                {
                    Sql = r.Query.Sql,
                    Title = r.Query.Title,
                    Parameters = r.Query.Parameters is { Count: > 0 } p
                        ? p.Select(x => new ParameterRef { Name = x.Keyword, Type = x.SqlType, Value = x.Value }).ToList()
                        : null,
                },
            Result = r.Result is null ? null : ResultRef.From(r.Result),
            SuggestedQuestions = r.SuggestedQuestions.Count == 0 ? null : r.SuggestedQuestions,
            DurationMs = (long)r.Metadata.Duration.TotalMilliseconds,
        };
    }

    private sealed record AgentRef
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed record QueryRef
    {
        [JsonPropertyName("sql")]
        public string? Sql { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        // The values Genie bound into the SQL. Without these a reader sees the statement but not
        // what it actually ran with, which is half the point of showing the SQL at all.
        [JsonPropertyName("parameters")]
        public IReadOnlyList<ParameterRef>? Parameters { get; init; }
    }

    private sealed record ParameterRef
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }

    private sealed record ResultRef
    {
        [JsonPropertyName("columns")]
        public required IReadOnlyList<ColumnRef> Columns { get; init; }

        [JsonPropertyName("rows")]
        public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

        [JsonPropertyName("truncated")]
        public required bool Truncated { get; init; }

        [JsonPropertyName("totalRowCount")]
        public long? TotalRowCount { get; init; }

        internal static ResultRef From(GenieQueryResult result) => new()
        {
            Columns = result.Columns
                .Select(c => new ColumnRef { Name = c.Name, Type = c.DataType })
                .ToList(),
            Rows = result.Rows,
            Truncated = result.IsTruncated,
            TotalRowCount = result.TotalRowCount,
        };
    }

    private sealed record ColumnRef
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}

/// <summary>Writes a query result as RFC 4180 CSV.</summary>
public static class CsvWriter
{
    // Characters a spreadsheet skips over before deciding a cell is a formula. OWASP names
    // tab and carriage return; a leading space is included for the same reason.
    private static readonly char[] FormulaLeadIn = ['\t', '\r', ' '];

    /// <summary>
    /// Formats the query result. Values are written exactly as Databricks returned them.
    /// </summary>
    /// <remarks>
    /// No locale formatting and no numeric parsing. A DECIMAL rendered through a double, or a
    /// thousands separator inserted for readability, changes the number a downstream tool
    /// reads. Only the CSV quoting rules are applied.
    /// </remarks>
    public static string Write(GenieQueryResult result)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, result);
        return writer.ToString();
    }

    /// <summary>Writes CSV incrementally, retaining at most one rendered row at a time.</summary>
    public static void Write(TextWriter writer, GenieQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var line in RenderLines(result))
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>Writes CSV asynchronously, retaining at most one rendered row at a time.</summary>
    public static async Task WriteAsync(
        TextWriter writer,
        GenieQueryResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        using var lines = RenderLines(result).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lines.MoveNext())
            {
                return;
            }

            await writer.WriteLineAsync(lines.Current.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> RenderLines(GenieQueryResult result)
    {
        yield return string.Join(',', result.Columns.Select(c => Quote(c.Name)));
        foreach (var row in result.Rows)
        {
            yield return string.Join(',', row.Select(Quote));
        }
    }

    /// <summary>
    /// Escapes a single CSV field, including the spreadsheet-formula guard.
    /// </summary>
    /// <remarks>
    /// Public so no other writer reimplements it. A second copy of this rule is how one CSV
    /// path ends up defused and another does not.
    /// </remarks>
    public static string EscapeField(string? value) => Quote(value);

    private static string Quote(string? value)
    {
        // A SQL NULL becomes an empty unquoted field, which is how every CSV reader
        // distinguishes it from the literal empty string written as "".
        if (value is null)
        {
            return string.Empty;
        }

        if (value.Length == 0)
        {
            return "\"\"";
        }

        // A leading =, +, - or @ makes a spreadsheet treat the cell as a formula. Tab and
        // carriage return count too: OWASP documents both as accepted prefixes before the
        // marker, and some spreadsheet versions honour them. Prefixing a single quote is the
        // conventional defence and is visible rather than silent.
        var lead = value.AsSpan().TrimStart(FormulaLeadIn);
        var needsFormulaGuard = lead.Length > 0 && lead[0] is '=' or '+' or '-' or '@';
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);

        if (needsFormulaGuard)
        {
            return $"\"'{escaped}\"";
        }

        return value.AsSpan().ContainsAny(",\"\r\n") ? $"\"{escaped}\"" : value;
    }
}

/// <summary>Writes a response as Markdown, for reports and pull-request comments.</summary>
public static class MarkdownWriter
{
    public static string Write(GenieResponse response, string? agentTitle = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, response, agentTitle);
        return writer.ToString();
    }

    /// <summary>Writes Markdown incrementally, retaining at most one rendered line at a time.</summary>
    public static void Write(TextWriter writer, GenieResponse response, string? agentTitle = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        foreach (var line in RenderLines(response, agentTitle))
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>Writes Markdown asynchronously, retaining at most one rendered line at a time.</summary>
    public static async Task WriteAsync(
        TextWriter writer,
        GenieResponse response,
        string? agentTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        using var lines = RenderLines(response, agentTitle).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lines.MoveNext())
            {
                return;
            }

            await writer.WriteLineAsync(lines.Current.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> RenderLines(GenieResponse response, string? agentTitle)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            yield return TerminalSafety.Sanitize(response.Text);
            yield return string.Empty;
        }

        if (response.Result is { Columns.Count: > 0 } result)
        {
            foreach (var line in RenderTableLines(result))
            {
                yield return line;
            }
        }

        if (response.Query is { Sql.Length: > 0 } query)
        {
            foreach (var line in RenderSqlLines(query))
            {
                yield return line;
            }
        }

        if (agentTitle is { Length: > 0 })
        {
            yield return $"_Agent: {agentTitle}_";
        }
    }

    /// <summary>
    /// Renders a result as a Markdown table plus its truncation notice.
    /// </summary>
    /// <remarks>
    /// Public so no other report writer reimplements it. A second copy of this had already
    /// drifted — one renderer stated the row counts on truncation and the other did not, so the
    /// same data produced two different claims about completeness depending on which command
    /// wrote it.
    /// </remarks>
    public static void WriteTable(StringBuilder builder, GenieQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(builder);
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteTable(writer, result);
    }

    /// <summary>Writes a Markdown table incrementally.</summary>
    public static void WriteTable(TextWriter writer, GenieQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var line in RenderTableLines(result))
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>Writes a Markdown table asynchronously, observing cancellation between rows.</summary>
    public static async Task WriteTableAsync(
        TextWriter writer,
        GenieQueryResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        using var lines = RenderTableLines(result).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lines.MoveNext())
            {
                return;
            }

            await writer.WriteLineAsync(
                lines.Current.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes generated SQL and its bound values using the canonical report format.</summary>
    public static void WriteSql(StringBuilder builder, GenieQuery query)
    {
        ArgumentNullException.ThrowIfNull(builder);
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteSql(writer, query);
    }

    /// <summary>Writes generated SQL and its bound values incrementally.</summary>
    public static void WriteSql(TextWriter writer, GenieQuery query)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(query);

        foreach (var line in RenderSqlLines(query))
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>Writes generated SQL and its bound values asynchronously.</summary>
    public static async Task WriteSqlAsync(
        TextWriter writer,
        GenieQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(query);

        using var lines = RenderSqlLines(query).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lines.MoveNext())
            {
                return;
            }

            await writer.WriteLineAsync(
                lines.Current.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> RenderTableLines(GenieQueryResult result)
    {
        yield return $"| {string.Join(" | ", result.Columns.Select(c => Escape(c.Name)))} |";
        yield return $"|{string.Concat(Enumerable.Repeat("---|", result.Columns.Count))}";

        foreach (var row in result.Rows)
        {
            yield return $"| {string.Join(" | ", row.Select(Escape))} |";
        }

        yield return string.Empty;

        if (result.IsTruncated)
        {
            var total = result.TotalRowCount is { } count
                ? count.ToString(CultureInfo.InvariantCulture)
                : "an unknown number of";
            yield return $"_Showing {result.RowCount} of {total} rows; the result was truncated by Databricks._";
            yield return string.Empty;
        }
    }

    private static IEnumerable<string> RenderSqlLines(GenieQuery query)
    {
        if (string.IsNullOrEmpty(query.Sql))
        {
            yield break;
        }

        yield return "<details><summary>Generated SQL</summary>";
        yield return string.Empty;
        yield return "```sql";
        yield return TerminalSafety.Sanitize(query.Sql);
        yield return "```";
        yield return string.Empty;

        if (query.Parameters is { Count: > 0 } parameters)
        {
            yield return "| Parameter | Type | Value |";
            yield return "|---|---|---|";
            foreach (var parameter in parameters)
            {
                yield return $"| {Escape(parameter.Keyword)} | {Escape(parameter.SqlType)} | {Escape(parameter.Value)} |";
            }

            yield return string.Empty;
        }

        yield return "</details>";
        yield return string.Empty;
    }

    // A null renders as an empty cell rather than the four letters "null", which would be
    // indistinguishable from a string containing them.
    private static string Escape(string? value) =>
        value is null
            ? string.Empty
            : TerminalSafety.SanitizeCell(value).Replace("|", "\\|", StringComparison.Ordinal);
}
