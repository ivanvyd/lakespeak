using System.CommandLine;
using System.Text.Json;
using LakeSpeak.Cli.Console;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

internal static class AgentsCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static Command Create()
    {
        var list = new Command("list", "List the Genie Agents this identity can see.");
        list.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, ListAsync, cancellationToken));

        var agents = new Command("agents", "Discover Genie Agents.");
        agents.Subcommands.Add(list);
        return agents;
    }

    private static async Task<int> ListAsync(CliHost host, CancellationToken cancellationToken)
    {
        host.Output.Status("Listing Genie Agents…");

        var agents = new List<GenieAgent>();
        await foreach (var agent in host.Client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(agent);
        }

        WriteListing(host.Output, host.Renderer, host.Format, agents);
        return ExitCode.Success;
    }

    internal static void WriteListing(
        ConsoleOutput output,
        TerminalRenderer renderer,
        OutputFormat format,
        IReadOnlyList<GenieAgent> agents)
    {
        if (agents.Count == 0)
        {
            // Not an error: an identity with no Genie grants legitimately sees nothing, and the
            // fix is a Databricks permission rather than anything this tool can do.
            output.Warn(
                "No Genie Agents are visible to this identity. Access is granted in Databricks.");
        }

        switch (format)
        {
            case OutputFormat.Json:
                output.WriteResultLine(JsonSerializer.Serialize(
                    agents.Select(a => new { id = a.AgentId, title = a.Title, description = a.Description }),
                    Indented));
                break;

            case OutputFormat.Jsonl:
                foreach (var agent in agents)
                {
                    output.WriteResultLine(JsonSerializer.Serialize(
                        new { id = agent.AgentId, title = agent.Title }));
                }

                break;

            case OutputFormat.Csv:
                output.WriteResultLine("id,title");
                foreach (var agent in agents)
                {
                    output.WriteResultLine($"{CsvWriter.EscapeField(agent.AgentId)},{CsvWriter.EscapeField(agent.Title)}");
                }

                break;

            // An Agent listing is the same table whichever human-facing format was asked for;
            // there is no per-format prose to render. Named rather than defaulted so a new
            // format has to be considered here instead of silently landing in this arm.
            case OutputFormat.Text:
            case OutputFormat.Table:
            case OutputFormat.Markdown:
                if (agents.Count > 0)
                {
                    renderer.WriteAgents(agents);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(format), format, "No renderer is wired up for this output format.");
        }
    }
}
