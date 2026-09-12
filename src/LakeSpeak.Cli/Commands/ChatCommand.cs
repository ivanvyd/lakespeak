using System.CommandLine;
using LakeSpeak.Cli.Console;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class ChatCommand
{
    private static readonly Option<string?> Agent =
        new("--agent", "-a") { Description = "Agent id, title, or a configured alias." };

    internal static Command Create()
    {
        var command = new Command("chat", "Hold a stateful conversation with a Genie Agent.")
        {
            Agent,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(
                parseResult,
                (host, ct) => RunAsync(host, parseResult, ct),
                cancellationToken,
                ProfileRequest(parseResult)));

        return command;
    }

    internal static CliProfileRequest ProfileRequest(ParseResult parseResult) =>
        CliProfileRequest.ForNewCommand(parseResult.GetValue(Agent));

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (!host.Output.IsInteractive)
        {
            // Piped input has no way to answer a selector or a confirmation, and a chat loop
            // reading EOF spins. `ask` is the scriptable entry point and is named in the error.
            throw new CliUsageException(
                "chat needs an interactive terminal. Use `lakespeak ask` for scripts and pipelines.");
        }

        var agent = await SelectAgentAsync(host, parseResult.GetValue(Agent), cancellationToken)
            .ConfigureAwait(false);

        var session = new ChatSession(host, agent);
        return await session.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GenieAgent> SelectAgentAsync(
        CliHost host, string? requested, CancellationToken cancellationToken)
    {
        requested ??= host.Config.Defaults.Agent;

        if (requested is { Length: > 0 })
        {
            var resolution = await host.Resolver.ResolveAsync(requested, cancellationToken).ConfigureAwait(false);
            if (resolution.Agent is not null)
            {
                return resolution.Agent;
            }

            host.Output.Warn($"No single Agent matches '{requested}'. Choose one below.");
        }

        var agents = new List<GenieAgent>();
        await foreach (var a in host.Client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(a);
        }

        if (agents.Count == 0)
        {
            throw new CliUsageException(
                "No Genie Agents are visible to this identity. Access is granted in Databricks.");
        }

        if (agents.Count == 1)
        {
            return agents[0];
        }

        // The selector is why nobody has to paste an Agent id to start talking.
        var choices = BuildAgentChoices(agents);
        var choice = host.Output.Error.Prompt(CreateAgentPrompt(choices));

        return choice.Agent;
    }

    internal static IReadOnlyList<AgentChoice> BuildAgentChoices(IReadOnlyList<GenieAgent> agents)
    {
        var duplicateTitles = agents
            .GroupBy(agent => agent.Title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return agents.Select(agent =>
        {
            var title = Markup.Escape(TerminalSafety.Sanitize(agent.Title));
            var label = duplicateTitles.Contains(agent.Title)
                ? $"{title} [dim]({Markup.Escape(TerminalSafety.Sanitize(agent.AgentId))})[/]"
                : title;
            return new AgentChoice(agent, label);
        }).ToList();
    }

    internal static SelectionPrompt<AgentChoice> CreateAgentPrompt(
        IEnumerable<AgentChoice> choices) =>
        new SelectionPrompt<AgentChoice>()
            .Title("Select a Genie Agent:")
            .PageSize(15)
            .MoreChoicesText("[dim](move to see more)[/]")
            .UseConverter(item => item.Label)
            .AddChoices(choices);
}

internal sealed record AgentChoice(GenieAgent Agent, string Label);

/// <summary>The interactive loop: prompt, ask, render, repeat.</summary>
internal sealed class ChatSession(CliHost host, GenieAgent agent)
{
    private GenieAgent _agent = agent;
    private string? _conversationId;
    private GenieResponse? _last;

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var console = host.Output.Error;
        console.MarkupLine($"[bold]LakeSpeak.NET[/] [dim]— independent, not a Databricks product[/]");
        console.MarkupLine(
            $"Agent: [bold]{Markup.Escape(TerminalSafety.Sanitize(_agent.Title))}[/]");
        console.MarkupLine("[dim]Type /help for commands, /exit to leave.[/]");
        console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            var input = console.Prompt(new TextPrompt<string>("[green]You:[/]").AllowEmpty()).Trim();

            if (input.Length == 0)
            {
                continue;
            }

            if (input.StartsWith('/'))
            {
                if (await HandleSlashAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    return ExitCode.Success;
                }

                continue;
            }

            await AskAsync(input, cancellationToken).ConfigureAwait(false);
        }

        return ExitCode.Success;
    }

    private async Task AskAsync(string question, CancellationToken cancellationToken)
    {
        var console = host.Output.Error;

        // Ctrl+C cancels the question in flight and returns to the prompt, rather than killing
        // the session. A Genie question can run for minutes and changing your mind is normal.
        using var perQuestion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            perQuestion.Cancel();
        };

        System.Console.CancelKeyPress += handler;
        try
        {
            var options = host.CreateAskOptions(
                state => console.MarkupLine($"[dim]{state.ToProgressDescription()}…[/]"));

            _last = _conversationId is null
                ? await AskNewAsync(question, options, perQuestion.Token).ConfigureAwait(false)
                : await host.Client.FollowUpAsync(
                    _agent.AgentId, _conversationId, question, options, perQuestion.Token).ConfigureAwait(false);

            Render(_last);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]Cancelled.[/] [dim]The question may still be running in Databricks.[/]");
        }
        catch (GenieException ex)
        {
            // Recoverable inside the session: a failed question should not end the conversation.
            host.Output.Fail(ex.Message);
        }
        finally
        {
            System.Console.CancelKeyPress -= handler;
        }
    }

    private async Task<GenieResponse> AskNewAsync(
        string question, GenieAskOptions options, CancellationToken cancellationToken)
    {
        var response = await host.Client.AskAsync(_agent.AgentId, question, options, cancellationToken)
            .ConfigureAwait(false);
        _conversationId = response.ConversationId;
        return response;
    }

    private void Render(GenieResponse response)
    {
        host.Output.Out.WriteLine();
        host.Renderer.WriteAnswer(response);

        if (response.Result is not null)
        {
            host.Renderer.WriteResult(response.Result);
        }

        CliResultCompleteness.WarnIfIncomplete(host.Output, response.Result);

        var hints = new List<string>();
        if (response.Query?.Sql is { Length: > 0 })
        {
            hints.Add("/sql");
        }

        if (response.Result is not null)
        {
            hints.Add("/result");
            hints.Add("/export");
        }

        if (hints.Count > 0)
        {
            host.Output.Error.MarkupLine($"[dim]{string.Join("  ", hints)}[/]");
        }

        host.Output.Error.WriteLine();
    }

    /// <returns>True when the session should end.</returns>
    private async Task<bool> HandleSlashAsync(string input, CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1].Trim() : null;
        var console = host.Output.Error;

        switch (command)
        {
            case "/exit":
            case "/quit":
                return true;

            case "/help":
                WriteHelp(console);
                return false;

            case "/new":
                _conversationId = null;
                _last = null;
                console.MarkupLine("[dim]Started a new conversation.[/]");
                return false;

            case "/agents":
                await ListAgentsAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case "/use":
                await UseAsync(argument, cancellationToken).ConfigureAwait(false);
                return false;

            case "/sql":
                if (_last?.Query?.Sql is { Length: > 0 } sql)
                {
                    host.Renderer.WriteSql(sql, _last?.Query?.Parameters);
                }
                else
                {
                    console.MarkupLine("[dim]The last answer had no generated SQL.[/]");
                }

                return false;

            case "/result":
                if (_last?.Result is { } result)
                {
                    host.Renderer.WriteResult(result);
                }
                else
                {
                    console.MarkupLine("[dim]The last answer had no query result.[/]");
                }

                return false;

            case "/export":
                await ExportAsync(argument, cancellationToken).ConfigureAwait(false);
                return false;

            case "/thumbs-up":
                await FeedbackAsync(GenieFeedbackRating.Positive, argument, cancellationToken).ConfigureAwait(false);
                return false;

            case "/thumbs-down":
                await FeedbackAsync(GenieFeedbackRating.Negative, argument, cancellationToken).ConfigureAwait(false);
                return false;

            default:
                console.MarkupLine($"[dim]Unknown command {Markup.Escape(command)}. Type /help.[/]");
                return false;
        }
    }

    private static void WriteHelp(IAnsiConsole console)
    {
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("cmd");
        table.AddColumn("what");
        table.AddRow("[bold]/help[/]", "Show this list");
        table.AddRow("[bold]/agents[/]", "List available Agents");
        table.AddRow("[bold]/use <agent>[/]", "Switch Agent and start a new conversation");
        table.AddRow("[bold]/new[/]", "Start a new conversation with this Agent");
        table.AddRow("[bold]/sql[/]", "Show the SQL behind the last answer");
        table.AddRow("[bold]/result[/]", "Show the last query result");
        table.AddRow("[bold]/export <path>[/]", "Write the last result to a CSV file");
        table.AddRow("[bold]/thumbs-up[/]", "Send positive feedback to Databricks");
        table.AddRow("[bold]/thumbs-down [comment][/]", "Send negative feedback to Databricks");
        table.AddRow("[bold]/exit[/]", "Leave (or /quit)");
        console.Write(table);
    }

    private async Task ListAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = new List<GenieAgent>();
        await foreach (var a in host.Client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(a);
        }

        host.Renderer.WriteAgents(agents);
    }

    private async Task UseAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            host.Output.Error.MarkupLine("[dim]Usage: /use <agent>[/]");
            return;
        }

        if (RequiresWorkspaceSwitch(host, name))
        {
            host.Output.Warn(
                "That Agent alias belongs to another workspace. Leave this chat and start a new " +
                "`lakespeak chat --agent <alias>` session so LakeSpeak can rebuild the authenticated client.");
            return;
        }

        var resolution = await host.Resolver.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        if (resolution.Agent is null)
        {
            host.Output.Warn(resolution.IsAmbiguous
                ? $"'{name}' matches more than one Agent; use the id."
                : $"No Agent matches '{name}'.");
            return;
        }

        _agent = resolution.Agent;
        // Switching Agent must start a new conversation: a conversation belongs to one Agent,
        // and carrying the id across would address a conversation the new Agent does not own.
        _conversationId = null;
        _last = null;
        host.Output.Error.MarkupLine(
            $"Now talking to [bold]{Markup.Escape(TerminalSafety.Sanitize(_agent.Title))}[/].");
    }

    internal static bool RequiresWorkspaceSwitch(CliHost host, string agentName) =>
        !CliWorkspaceResolver.IsSameWorkspace(
            host.Workspace,
            host.ResolveWorkspaceForAgentSwitch(agentName));

    private async Task ExportAsync(string? path, CancellationToken cancellationToken)
    {
        if (_last?.Result is not { } result)
        {
            host.Output.Error.MarkupLine("[dim]The last answer had no query result to export.[/]");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            host.Output.Error.MarkupLine("[dim]Usage: /export <path.csv>[/]");
            return;
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full)
            && !host.Output.Error.Confirm($"Overwrite {Markup.Escape(full)}?", defaultValue: false))
        {
            return;
        }

        await ExportCommand.WriteCsvFileAtomicAsync(
            full,
            result,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
        host.Output.Error.MarkupLine(
            $"[green]Wrote[/] {Markup.Escape(full)} [dim]({Wording.Count(result.RowCount, "row")}). " +
            "It contains governed data; look after it.[/]");
        CliResultCompleteness.WarnIfIncomplete(host.Output, result);
    }

    private async Task FeedbackAsync(GenieFeedbackRating rating, string? comment, CancellationToken cancellationToken)
    {
        if (_last is null)
        {
            host.Output.Error.MarkupLine("[dim]Ask something first.[/]");
            return;
        }

        await host.Client.SendFeedbackAsync(
            _last.AgentId, _last.ConversationId, _last.MessageId, rating, comment, cancellationToken)
            .ConfigureAwait(false);

        host.Output.Error.MarkupLine("[dim]Feedback sent to Databricks.[/]");
    }
}
