using System.Globalization;
using System.Text;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.QuestionPacks;

public sealed record QuestionOutcome(
    PackQuestion Question,
    GenieResponse? Response,
    string? Failure,
    TimeSpan Duration)
{
    public bool Succeeded => Response is not null;
}

public sealed record PackResult(
    QuestionPack Pack,
    string AgentId,
    string? AgentTitle,
    IReadOnlyList<QuestionOutcome> Outcomes,
    DateTimeOffset StartedAt,
    TimeSpan Duration)
{
    public int FailureCount => Outcomes.Count(o => !o.Succeeded);

    public bool AnyFailed => FailureCount > 0;
}

/// <summary>An outcome without the potentially large response graph retained by <see cref="QuestionOutcome"/>.</summary>
internal sealed record QuestionOutcomeSummary(
    PackQuestion Question,
    bool Succeeded,
    string? Failure,
    TimeSpan Duration);

/// <summary>Compact result returned by the streaming pack path used by the CLI.</summary>
internal sealed record PackRunSummary(
    QuestionPack Pack,
    string AgentId,
    string? AgentTitle,
    IReadOnlyList<QuestionOutcomeSummary> Outcomes,
    DateTimeOffset StartedAt,
    TimeSpan Duration)
{
    public int FailureCount => Outcomes.Count(o => !o.Succeeded);

    public bool AnyFailed => FailureCount > 0;
}

/// <summary>
/// Runs a pack's questions in order and collects the outcomes.
/// </summary>
/// <remarks>
/// <para>
/// Questions run sequentially rather than in parallel. Each one occupies a SQL warehouse, and a
/// pack that fires ten concurrent questions at a shared warehouse degrades it for everyone else
/// on it. Reports are not latency-sensitive.
/// </para>
/// <para>
/// Each question gets a fresh conversation. Genie conversations are stateful, so reusing one
/// would let the answer to question three depend on questions one and two — making the report
/// non-deterministic and order-dependent in a way nobody reading it would suspect.
/// </para>
/// </remarks>
public sealed class PackRunner(IGenieClient client, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PackResult> RunAsync(
        QuestionPack pack,
        string agentId,
        string? agentTitle,
        IProgress<PackQuestion>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<QuestionOutcome>(pack.Questions.Count);
        var summary = await RunCoreAsync(
            pack,
            agentId,
            agentTitle,
            (outcome, _) =>
            {
                outcomes.Add(outcome);
                return ValueTask.CompletedTask;
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        return new PackResult(
            pack, agentId, agentTitle, outcomes, summary.StartedAt, summary.Duration);
    }

    /// <summary>
    /// Runs questions sequentially and releases each response after the caller has emitted it.
    /// </summary>
    internal Task<PackRunSummary> RunStreamingAsync(
        QuestionPack pack,
        string agentId,
        string? agentTitle,
        Func<QuestionOutcome, CancellationToken, ValueTask> onOutcome,
        IProgress<PackQuestion>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onOutcome);
        return RunCoreAsync(
            pack, agentId, agentTitle, onOutcome, progress, cancellationToken);
    }

    private async Task<PackRunSummary> RunCoreAsync(
        QuestionPack pack,
        string agentId,
        string? agentTitle,
        Func<QuestionOutcome, CancellationToken, ValueTask> onOutcome,
        IProgress<PackQuestion>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var packStart = _time.GetTimestamp();
        var outcomes = new List<QuestionOutcomeSummary>(pack.Questions.Count);

        foreach (var question in pack.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(question);

            var start = _time.GetTimestamp();
            QuestionOutcome outcome;
            try
            {
                var response = await client.AskAsync(
                    agentId,
                    question.Ask,
                    new GenieAskOptions
                    {
                        IncludeQueryResult = true,
                        Timeout = question.Timeout ?? pack.Behavior.Timeout,
                    },
                    cancellationToken).ConfigureAwait(false);

                outcome = new QuestionOutcome(
                    question, response, null, _time.GetElapsedTime(start));
            }
            catch (GenieException ex)
            {
                if (!pack.Behavior.ContinueOnQuestionFailure)
                {
                    throw;
                }

                // The message is already scrubbed by GenieException, so it is safe to put in a
                // report that gets committed or pasted into a ticket.
                outcome = new QuestionOutcome(
                    question, null, ex.Message, _time.GetElapsedTime(start));
            }

            await onOutcome(outcome, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new QuestionOutcomeSummary(
                question, outcome.Succeeded, outcome.Failure, outcome.Duration));
        }

        return new PackRunSummary(
            pack, agentId, agentTitle, outcomes, startedAt, _time.GetElapsedTime(packStart));
    }
}

/// <summary>
/// Writes a pack result as Markdown.
/// </summary>
/// <remarks>
/// The structure is deterministic: the same pack against the same data produces a byte-identical
/// report except for the timestamp and timings. That is what makes a committed report reviewable
/// in a diff.
/// </remarks>
public static class PackReportWriter
{
    public static string WriteMarkdown(PackResult result, string toolVersion)
    {
        var builder = new StringBuilder(WriteHeader(
            result.Pack,
            result.AgentId,
            result.AgentTitle,
            result.StartedAt,
            result.FailureCount,
            result.Outcomes.Count,
            toolVersion));

        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        foreach (var outcome in result.Outcomes)
        {
            WriteSection(writer, outcome, result.Pack);
        }

        return builder.ToString();
    }

    /// <summary>Writes the bounded report preamble after all outcome statuses are known.</summary>
    internal static string WriteHeader(PackRunSummary result, string toolVersion) => WriteHeader(
        result.Pack,
        result.AgentId,
        result.AgentTitle,
        result.StartedAt,
        result.FailureCount,
        result.Outcomes.Count,
        toolVersion);

    /// <summary>Writes one completed section without retaining its rendered rows.</summary>
    internal static async Task WriteSectionAsync(
        TextWriter writer,
        QuestionOutcome outcome,
        QuestionPack pack,
        CancellationToken cancellationToken)
    {
        var heading = outcome.Question.Title ?? outcome.Question.Id;
        await writer.WriteLineAsync(
            $"<a id=\"{outcome.Question.Id}\"></a>".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(
            $"## {TerminalSafety.Sanitize(heading)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            await writer.WriteLineAsync(
                $"**This question failed.** {TerminalSafety.Sanitize(outcome.Failure)}".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = outcome.Response!;
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            await writer.WriteLineAsync(
                TerminalSafety.Sanitize(response.Text).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        }

        if (response.Result is { Columns.Count: > 0 } table)
        {
            await MarkdownWriter.WriteTableAsync(writer, table, cancellationToken).ConfigureAwait(false);
        }

        if (pack.Behavior.IncludeGeneratedSql && response.Query is { Sql.Length: > 0 } query)
        {
            await MarkdownWriter.WriteSqlAsync(writer, query, cancellationToken).ConfigureAwait(false);
        }

        var notes = Notes(outcome, pack, response);
        if (notes.Count > 0)
        {
            await writer.WriteLineAsync(
                $"_{string.Join(" · ", notes)}_".AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string WriteHeader(
        QuestionPack pack,
        string agentId,
        string? agentTitle,
        DateTimeOffset startedAt,
        int failureCount,
        int outcomeCount,
        string toolVersion)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {Title(pack)}")
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(pack.Description))
        {
            builder.AppendLine(TerminalSafety.Sanitize(pack.Description)).AppendLine();
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
                $"Generated: {startedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
            .AppendLine(CultureInfo.InvariantCulture,
                $"Agent: {TerminalSafety.Sanitize(agentTitle ?? agentId)}")
            .AppendLine(CultureInfo.InvariantCulture, $"LakeSpeak: {toolVersion}")
            .AppendLine();

        if (failureCount > 0)
        {
            // Stated at the top, not buried after the answers that did work. A report whose
            // failures are only visible at the bottom gets read as complete.
            builder.AppendLine(CultureInfo.InvariantCulture,
                    $"> **{failureCount} of {outcomeCount} questions failed.** Sections below say which, and why.")
                .AppendLine();
        }

        builder.AppendLine("Answers are generated by Databricks Genie from natural-language questions. " +
                "They can be wrong in ways that read as plausible. Check the generated SQL before " +
                "acting on anything consequential.")
            .AppendLine();

        return builder.ToString();
    }

    private static void WriteSection(TextWriter writer, QuestionOutcome outcome, QuestionPack pack)
    {
        var heading = outcome.Question.Title ?? outcome.Question.Id;
        writer.WriteLine($"<a id=\"{outcome.Question.Id}\"></a>");
        writer.WriteLine($"## {TerminalSafety.Sanitize(heading)}");
        writer.WriteLine();

        if (!outcome.Succeeded)
        {
            writer.WriteLine($"**This question failed.** {TerminalSafety.Sanitize(outcome.Failure)}");
            writer.WriteLine();
            return;
        }

        var response = outcome.Response!;

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            writer.WriteLine(TerminalSafety.Sanitize(response.Text));
            writer.WriteLine();
        }

        if (response.Result is { Columns.Count: > 0 } table)
        {
            // The canonical renderer, not a copy. A second implementation had already drifted:
            // this report omitted the row counts that `ask --format markdown` states, so the
            // same data made two different claims about how complete it was.
            MarkdownWriter.WriteTable(writer, table);
        }

        if (pack.Behavior.IncludeGeneratedSql && response.Query is { Sql.Length: > 0 } query)
        {
            MarkdownWriter.WriteSql(writer, query);
        }

        var notes = Notes(outcome, pack, response);
        if (notes.Count > 0)
        {
            writer.WriteLine($"_{string.Join(" · ", notes)}_");
            writer.WriteLine();
        }
    }

    private static List<string> Notes(
        QuestionOutcome outcome,
        QuestionPack pack,
        GenieResponse response)
    {
        var notes = new List<string>();
        if (pack.Behavior.IncludeTimings)
        {
            notes.Add(outcome.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
        }

        if (pack.Behavior.IncludeIdentifiers)
        {
            notes.Add($"conversation `{TerminalSafety.Sanitize(response.ConversationId)}`");
            notes.Add($"message `{TerminalSafety.Sanitize(response.MessageId)}`");
        }

        return notes;
    }

    private static string Title(QuestionPack pack) =>
        string.Join(' ', pack.Name.Split('-').Select(w =>
            w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
