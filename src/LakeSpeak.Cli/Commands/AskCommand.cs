using System.CommandLine;
using LakeSpeak.Cli.Console;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

internal static class AskCommand
{
    private static readonly Argument<string> Question =
        new("question") { Description = "The question to ask." };

    private static readonly Option<string?> Agent =
        new("--agent", "-a") { Description = "Agent id, title, or a configured alias." };

    private static readonly Option<bool> ShowSql =
        new("--show-sql") { Description = "Print the generated SQL alongside the answer." };

    internal static Command Create()
    {
        var command = new Command("ask", "Ask a question and print the answer.")
        {
            Question,
            Agent,
            ShowSql,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) =>
                RunAsync(host, parseResult, ct), cancellationToken, ProfileRequest(parseResult)));

        return command;
    }

    internal static CliProfileRequest ProfileRequest(ParseResult parseResult) =>
        CliProfileRequest.ForNewCommand(parseResult.GetValue(Agent));

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var question = parseResult.GetValue(Question)
            ?? throw new CliUsageException("A question is required.");

        var agentName = parseResult.GetValue(Agent) ?? host.Config.Defaults.Agent
            ?? throw new CliUsageException(
                "No Agent specified. Pass --agent, or set defaults.agent in your config file. " +
                "Run `lakespeak agents list` to see what is available.");

        var resolution = await host.Resolver.ResolveAsync(agentName, cancellationToken).ConfigureAwait(false);

        if (resolution.IsAmbiguous)
        {
            // Never guessed, in any mode. Picking the first of two Agents called "Finance"
            // would answer against the wrong data and look like it worked.
            var ids = string.Join(", ", resolution.Candidates.Select(c => c.AgentId));
            throw new CliUsageException(
                $"'{agentName}' matches {resolution.Candidates.Count} Agents ({ids}). Use the id.");
        }

        if (resolution.NotFound)
        {
            throw new CliUsageException(
                $"No Genie Agent matches '{agentName}'. Run `lakespeak agents list` to see what is available.");
        }

        var agent = resolution.Agent!;
        var showSql = parseResult.GetValue(ShowSql) || host.Config.Display.ShowSqlByDefault;

        var response = await host.Client.AskAsync(
            agent.AgentId,
            question,
            host.CreateAskOptions(
                state => host.Output.Status(state.ToProgressDescription() + "…")),
            cancellationToken).ConfigureAwait(false);

        new RecentConversation
        {
            Profile = host.Workspace.Profile,
            WorkspaceHost = CliWorkspaceResolver.WorkspaceIdentity(host.Workspace.Host),
            AgentId = agent.AgentId,
            AgentTitle = agent.Title,
            ConversationId = response.ConversationId,
            MessageId = response.MessageId,
            AttachmentId = response.Metadata.AttachmentId,
            UpdatedAt = host.Clock.GetUtcNow(),
        }.Save();

        await WriteAsync(
            host.Output,
            host.Renderer,
            host.Format,
            response,
            agent,
            showSql,
            cancellationToken).ConfigureAwait(false);
        return ExitCode.Success;
    }

    internal static async Task WriteAsync(
        ConsoleOutput output,
        TerminalRenderer renderer,
        OutputFormat format,
        GenieResponse response,
        GenieAgent agent,
        bool showSql,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case OutputFormat.Json:
                var json = MachineOutput.ToJson(response, agent.Title);
                await output.ResultWriter.WriteLineAsync(
                    json.AsMemory(), cancellationToken).ConfigureAwait(false);
                break;

            case OutputFormat.Jsonl:
                await MachineOutput.WriteJsonLinesAsync(
                    output.ResultWriter,
                    response,
                    agent.Title,
                    cancellationToken).ConfigureAwait(false);
                break;

            case OutputFormat.Csv:
                if (response.Result is null)
                {
                    // CSV means the query result, and there is no honest way to render prose as
                    // rows. Saying so on stderr keeps stdout empty and parseable.
                    output.Warn("This answer has no query result, so there is nothing to write as CSV.");
                    break;
                }

                await CsvWriter.WriteAsync(
                    output.ResultWriter,
                    response.Result,
                    cancellationToken).ConfigureAwait(false);
                break;

            case OutputFormat.Markdown:
                await MarkdownWriter.WriteAsync(
                    output.ResultWriter,
                    response,
                    agent.Title,
                    cancellationToken).ConfigureAwait(false);
                break;

            case OutputFormat.Table:
                if (response.Result is not null)
                {
                    renderer.WriteResult(response.Result);
                }

                break;

            case OutputFormat.Text:
                renderer.WriteAnswer(response);
                if (response.Result is not null)
                {
                    renderer.WriteResult(response.Result);
                }

                if (showSql && response.Query?.Sql is { Length: > 0 } sql)
                {
                    renderer.WriteSql(sql, response.Query?.Parameters);
                }

                if (response.State == GenieMessageState.QueryResultExpired)
                {
                    output.Warn(
                        "The cached query result has expired, so no table is shown. The answer and SQL are still valid.");
                }

                break;

            // Not a silent fall-through to Text. A format added to the enum and forgotten here
            // would otherwise render as plain text and look like a working feature — the same
            // reason ExitCode.From throws rather than defaulting.
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(format), format, "No renderer is wired up for this output format.");
        }

        CliResultCompleteness.WarnIfIncomplete(output, response.Result);
    }
}
