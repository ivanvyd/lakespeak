using LakeSpeak.Genie;

namespace LakeSpeak.QuestionPacks.Tests;

public sealed class PackReportWriterTests
{
    [Fact]
    public void Uses_question_ids_as_stable_anchors_and_includes_sql_parameters()
    {
        var pack = Pack() with
        {
            Questions = [new PackQuestion("stable-id", "A title that can change", "ask", null)],
        };
        var result = Result(pack, [new QuestionOutcome(
            pack.Questions[0],
            Response(new GenieQuery(
                "SELECT * FROM sales WHERE region = :region",
                Parameters: [new GenieQueryParameter("region", "STRING", "West")])) ,
            null,
            TimeSpan.FromSeconds(1))]);

        var markdown = PackReportWriter.WriteMarkdown(result, "0.3.1");

        markdown.ShouldContain("<a id=\"stable-id\"></a>");
        markdown.ShouldContain("## A title that can change");
        markdown.ShouldContain("SELECT * FROM sales WHERE region = :region");
        markdown.ShouldContain("| region | STRING | West |");
    }

    [Fact]
    public void Failure_summary_is_above_answers_and_sections_keep_pack_order()
    {
        var pack = Pack();
        var result = Result(pack,
        [
            new QuestionOutcome(pack.Questions[0], null, "failed", TimeSpan.Zero),
            new QuestionOutcome(pack.Questions[1], Response(), null, TimeSpan.Zero),
        ]);

        var markdown = PackReportWriter.WriteMarkdown(result, "0.3.1");

        markdown.IndexOf("1 of 2 questions failed", StringComparison.Ordinal)
            .ShouldBeLessThan(markdown.IndexOf("<a id=\"first\"></a>", StringComparison.Ordinal));
        markdown.IndexOf("<a id=\"first\"></a>", StringComparison.Ordinal)
            .ShouldBeLessThan(markdown.IndexOf("<a id=\"second\"></a>", StringComparison.Ordinal));
    }

    [Fact]
    public void Compact_streaming_summary_writes_the_same_header_contract()
    {
        var pack = Pack();
        var summary = new PackRunSummary(
            pack,
            "agent-1",
            "Agent",
            [
                new QuestionOutcomeSummary(pack.Questions[0], false, "failed", TimeSpan.Zero),
                new QuestionOutcomeSummary(pack.Questions[1], true, null, TimeSpan.Zero),
            ],
            new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(2));

        var header = PackReportWriter.WriteHeader(summary, "0.3.1");

        header.ShouldContain("1 of 2 questions failed");
        header.ShouldContain("Generated: 2026-09-12 10:00 UTC");
        header.ShouldNotContain("## ");
    }

    [Fact]
    public void Report_behavior_flags_control_sql_timings_and_identifiers()
    {
        var pack = Pack() with
        {
            Behavior = new PackBehavior(
                ContinueOnQuestionFailure: true,
                IncludeGeneratedSql: false,
                IncludeTimings: false,
                IncludeIdentifiers: false,
                Timeout: TimeSpan.FromMinutes(10)),
        };
        var result = Result(pack, [new QuestionOutcome(
            pack.Questions[0],
            Response(new GenieQuery("SELECT 1")),
            null,
            TimeSpan.FromSeconds(1.2))]);

        var markdown = PackReportWriter.WriteMarkdown(result, "0.3.1");

        markdown.ShouldNotContain("SELECT 1");
        markdown.ShouldNotContain("conversation `");
        markdown.ShouldNotContain("message `");
        markdown.ShouldNotContain("1.2s");
    }

    [Fact]
    public async Task Streaming_header_and_sections_match_the_in_memory_report()
    {
        var pack = Pack();
        var outcomes = new QuestionOutcome[]
        {
            new(pack.Questions[0], null, "failed", TimeSpan.FromSeconds(1)),
            new(pack.Questions[1], Response(), null, TimeSpan.FromSeconds(2)),
        };
        var result = Result(pack, outcomes);
        var summary = new PackRunSummary(
            pack,
            result.AgentId,
            result.AgentTitle,
            outcomes.Select(o => new QuestionOutcomeSummary(
                o.Question, o.Succeeded, o.Failure, o.Duration)).ToArray(),
            result.StartedAt,
            result.Duration);

        using var sections = new StringWriter();
        foreach (var outcome in outcomes)
        {
            await PackReportWriter.WriteSectionAsync(
                sections, outcome, pack, TestContext.Current.CancellationToken);
        }

        var streamed = PackReportWriter.WriteHeader(summary, "0.3.1") + sections.ToString();

        streamed.ShouldBe(PackReportWriter.WriteMarkdown(result, "0.3.1"));
    }

    private static QuestionPack Pack() => new(
        "pack-name", "Description", "agent", null,
        [
            new PackQuestion("first", "First title", "first", null),
            new PackQuestion("second", "Second title", "second", null),
        ],
        new PackOutput("markdown", null),
        new PackBehavior(true, true, true, true, TimeSpan.FromMinutes(10)));

    private static PackResult Result(QuestionPack pack, IReadOnlyList<QuestionOutcome> outcomes) => new(
        pack,
        "agent-1",
        "Agent",
        outcomes,
        new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero),
        TimeSpan.FromSeconds(2));

    private static GenieResponse Response(GenieQuery? query = null) => new(
        "agent-1", "conversation", "message", GenieMessageState.Completed, "answer", query, null, [],
        new GenieResponseMetadata(TimeSpan.Zero, 1));
}
