using LakeSpeak.Rendering;

namespace LakeSpeak.Genie.Tests;

/// <summary>
/// Values must reach the output byte-for-byte as Databricks returned them. These tests exist
/// because the failure mode is silent: a reformatted number or a mangled character still looks
/// like a working report.
/// </summary>
public class OutputFidelityTests
{
    private static GenieQueryResult Result(params (string Name, string? Value)[] cells) =>
        new(
            cells.Select(c => new GenieColumn(c.Name, "STRING", "STRING")).ToList(),
            [cells.Select(c => c.Value).ToList()],
            IsTruncated: false,
            TotalRowCount: 1);

    private static GenieResponse Response(string? text, GenieQueryResult? result) =>
        new("agent", "conversation", "message", GenieMessageState.Completed,
            text, null, result, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

    [Theory]
    [InlineData("4500000.00")]
    [InlineData("3350000.50")]
    [InlineData("0.000000000000000001")]
    [InlineData("-0.0")]
    [InlineData("1E+40")]
    [InlineData("99999999999999999999999999.99")]
    public void Csv_carries_numbers_through_unchanged(string value)
    {
        // Arrange
        var result = Result(("amount", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert — not 4.5E6, not 4,500,000.00, not 4500000. Parsing a DECIMAL in order to
        // print it is how a client silently changes someone's revenue figure.
        csv.ShouldContain(value);
    }

    [Theory]
    [InlineData("€2.4M")]
    [InlineData("Ökonomie")]
    [InlineData("東京")]
    [InlineData("Ω≈ç√∫")]
    [InlineData("emoji 🙂 in a cell")]
    public void Non_ascii_survives_every_writer(string value)
    {
        // Arrange
        var result = Result(("label", value));
        var response = Response(value, result);

        // Act
        var csv = CsvWriter.Write(result);
        var markdown = MarkdownWriter.Write(response);
        var sanitized = TerminalSafety.Sanitize(value);
        using var json = System.Text.Json.JsonDocument.Parse(MachineOutput.ToJson(response));

        // Assert — JSON is checked after a round trip rather than by substring: characters
        // outside the BMP are legitimately written as escaped surrogate pairs, which every
        // parser decodes, so a substring check would fail on correct output.
        csv.ShouldContain(value);
        markdown.ShouldContain(value);
        sanitized.ShouldBe(value);
        json.RootElement.GetProperty("answer").GetString().ShouldBe(value);
    }

    [Fact]
    public void Csv_distinguishes_null_from_empty_string()
    {
        // Arrange — a SQL NULL and the empty string are different values, and a format that
        // renders them identically loses information the caller cannot recover.
        var result = Result(("a", null), ("b", string.Empty));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
        var dataLine = csv.Split('\n')[1].TrimEnd('\r');
        dataLine.ShouldBe(",\"\"");
    }

    [Fact]
    public void Json_lines_rejects_duplicate_column_names_before_writing_any_rows()
    {
        // Arrange — a JSON object cannot carry both cells under the same property name. The
        // previous dictionary writer silently replaced the first value with the second.
        var response = Response(null, Result(("amount", "first"), ("amount", "second")));

        // Act
        using var destination = new StringWriter();
        var act = () => MachineOutput.WriteJsonLines(destination, response);

        // Assert
        var exception = Should.Throw<InvalidOperationException>(act);
        exception.Message.ShouldContain("amount");
        exception.Message.ShouldContain("duplicate", Case.Insensitive);
        destination.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Json_lines_omits_cells_missing_from_a_short_row()
    {
        var result = new GenieQueryResult(
            [
                new GenieColumn("present", "STRING", "STRING"),
                new GenieColumn("missing", "STRING", "STRING"),
            ],
            [["value"]],
            IsTruncated: false,
            TotalRowCount: 1);

        using var row = System.Text.Json.JsonDocument.Parse(
            MachineOutput.ToJsonLines(Response(null, result)));

        row.RootElement.GetProperty("present").GetString().ShouldBe("value");
        row.RootElement.TryGetProperty("missing", out _).ShouldBeFalse();
    }

    [Fact]
    public void Json_lines_for_a_zero_row_result_is_an_explicit_empty_stream()
    {
        var result = new GenieQueryResult(
            [new GenieColumn("value", "STRING", "STRING")],
            [],
            IsTruncated: false,
            TotalRowCount: 0);

        MachineOutput.ToJsonLines(Response(null, result)).ShouldBeEmpty();
    }

    [Fact]
    public void Json_lines_rejects_cells_beyond_the_declared_columns()
    {
        // Arrange — without an explicit check the second cell is silently dropped because no
        // JSON property exists to name it.
        var result = new GenieQueryResult(
            [new GenieColumn("first", "STRING", "STRING")],
            [["kept", "lost"]],
            IsTruncated: false,
            TotalRowCount: 1);
        var response = Response(null, result);

        // Act
        var act = () => MachineOutput.ToJsonLines(response);

        // Assert
        Should.Throw<InvalidOperationException>(act)
            .Message.ShouldContain("2 cells");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    public void Csv_defuses_spreadsheet_formulas(string value)
    {
        // Arrange
        var result = Result(("payload", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert — the value is still present and readable; it just cannot execute.
        csv.ShouldContain($"\"'{value}\"");
    }

    [Theory]
    [InlineData("\t=cmd|' /c calc'!A1")]
    [InlineData("\r=1+1")]
    public void Csv_defuses_a_formula_hidden_behind_leading_whitespace(string value)
    {
        // Arrange — OWASP documents tab and carriage return as accepted prefixes before the
        // formula marker; a guard that only checks value[0] misses both.
        var result = Result(("payload", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
        csv.ShouldContain("'");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("\t=cmd|'/c calc'!A1")]
    [InlineData("has,comma")]
    [InlineData(null)]
    public void The_shared_escaper_carries_the_same_rules_as_the_row_writer(string? value)
    {
        // Arrange — every CSV path must go through one escaper. A second copy is how one path
        // ends up defused against formula injection and another does not, which is exactly what
        // happened to `agents list --format csv`.
        var viaRowWriter = CsvWriter.Write(Result(("payload", value))).Split('\n')[1].TrimEnd('\r');

        // Act
        var viaSharedHelper = CsvWriter.EscapeField(value);

        // Assert
        viaSharedHelper.ShouldBe(viaRowWriter);
    }

    [Fact]
    public void Csv_quotes_values_containing_delimiters_and_quotes()
    {
        // Arrange
        var result = Result(("a", "has,comma"), ("b", "has\"quote"));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
        csv.ShouldContain("\"has,comma\"");
        csv.ShouldContain("\"has\"\"quote\"");
    }

    [Theory]
    [InlineData("[2Jcleared")]
    [InlineData("bell")]
    [InlineData("carriage\rreturn")]
    public void Control_characters_are_neutralised(string value)
    {
        // Arrange — Genie returns model-generated prose and cells drawn from your tables. A
        // crafted value must not be able to move the cursor or draw this tool's own prompt.

        // Act
        var sanitized = TerminalSafety.SanitizeCell(value);

        // Assert
        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("\r");
    }

    [Fact]
    public void Newline_and_tab_survive_sanitising_prose()
    {
        // Arrange
        const string prose = "line one\nline two\tcolumn";

        // Act
        var sanitized = TerminalSafety.Sanitize(prose);

        // Assert
        sanitized.ShouldBe(prose);
    }

    [Fact]
    public void Markdown_escapes_pipes_so_a_cell_cannot_break_the_table()
    {
        // Arrange
        var response = Response(null, Result(("col", "a|b")));

        // Act
        var markdown = MarkdownWriter.Write(response);

        // Assert
        markdown.ShouldContain("a\\|b");
    }

    [Fact]
    public void Json_carries_the_values_bound_into_the_sql()
    {
        // Arrange — the bind values were captured from the wire and then dropped before any
        // renderer saw them, so a reader got the statement but not what it actually ran with.
        var response = new GenieResponse(
            "a", "c", "m", GenieMessageState.Completed, "answer",
            new GenieQuery("SELECT * FROM t WHERE region = :region", Parameters:
            [
                new GenieQueryParameter("region", "STRING", "Germany"),
            ]),
            null, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

        // Act
        using var parsed = System.Text.Json.JsonDocument.Parse(MachineOutput.ToJson(response));

        // Assert
        var parameter = parsed.RootElement.GetProperty("query").GetProperty("parameters")[0];
        parameter.GetProperty("name").GetString().ShouldBe("region");
        parameter.GetProperty("type").GetString().ShouldBe("STRING");
        parameter.GetProperty("value").GetString().ShouldBe("Germany");
    }

    [Fact]
    public void Json_omits_parameters_entirely_when_the_query_had_none()
    {
        // Arrange — an empty array would imply Genie reported zero bind values; absent is the
        // honest encoding of "not applicable".
        var response = new GenieResponse(
            "a", "c", "m", GenieMessageState.Completed, "answer",
            new GenieQuery("SELECT 1"), null, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

        // Act
        using var parsed = System.Text.Json.JsonDocument.Parse(MachineOutput.ToJson(response));

        // Assert
        parsed.RootElement.GetProperty("query")
            .TryGetProperty("parameters", out _).ShouldBeFalse();
    }

    [Fact]
    public void Json_uses_a_stable_lowercase_status_vocabulary()
    {
        // Arrange
        var response = new GenieResponse(
            "a", "c", "m", GenieMessageState.QueryResultExpired, null, null, null, [],
            new GenieResponseMetadata(TimeSpan.Zero, 1));

        // Act
        var json = MachineOutput.ToJson(response);

        // Assert
        json.ShouldContain("\"status\": \"queryresultexpired\"");
        json.ShouldContain("\"schemaVersion\": \"1\"");
    }
}
