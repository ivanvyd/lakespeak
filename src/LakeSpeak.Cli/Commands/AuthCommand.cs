using System.CommandLine;
using LakeSpeak.Cli.Console;
using LakeSpeak.Genie.Authentication;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class AuthCommand
{
    internal static Command Create()
    {
        var check = new Command("check", "Verify that credentials and the workspace host resolve.");
        check.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, CheckAsync, cancellationToken));

        var auth = new Command("auth", "Inspect authentication.");
        auth.Subcommands.Add(check);
        return auth;
    }

    private static async Task<int> CheckAsync(CliHost host, CancellationToken cancellationToken)
    {
        var profiles = DatabricksProfiles.Load();

        host.Output.Error.MarkupLine($"Profiles in .databrickscfg: [bold]{profiles.Count}[/]");

        foreach (var p in profiles)
        {
            var host_ = p.Host?.Host ?? "(no host)";
            host.Output.Error.MarkupLine($"  [dim]{Spectre.Console.Markup.Escape(p.Name)}[/] → {Spectre.Console.Markup.Escape(host_)}");

            if (p.UsesLegacyToken)
            {
                // Flagged, not blocked. A PAT is a standing credential with no expiry pressure,
                // and Databricks documents it as a local-debugging path rather than a
                // production one.
                host.Output.Warn(
                    $"Profile '{p.Name}' holds a personal access token. Prefer `databricks auth login` for OAuth.");
            }
        }

        // The only real proof is fetching a token. Everything above is configuration reading.
        // Ask the provider the client will actually use — constructing the CLI broker here would
        // report failure when DATABRICKS_TOKEN is set and the Databricks CLI is not installed,
        // which is precisely the unattended setup the environment provider exists to serve.
        var provider = host.TokenProvider;
        host.Output.Status($"Requesting a token via {DescribeProvider(provider)}…");

        var token = await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        // Length only. Printing any part of a token to a terminal puts it in scrollback and,
        // often, in someone's pasted bug report.
        host.Output.Error.MarkupLine(
            $"[green]OK[/] — obtained a token ([dim]{token.Length} characters, not shown[/]).");

        var agents = 0;
        await foreach (var _ in host.Client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            agents++;
        }

        host.Output.Error.MarkupLine($"[green]OK[/] — the workspace answered; [bold]{Wording.Count(agents, "Agent")}[/] visible.");
        return ExitCode.Success;
    }

    // Naming the source is the diagnostic value of this command: "it worked" is much less useful
    // than "it worked via DATABRICKS_TOKEN", when the reason for running it is usually that
    // something picked up a credential the reader did not expect.
    private static string DescribeProvider(IGenieTokenProvider provider) => provider switch
    {
        DatabricksCliTokenProvider => "the Databricks CLI",
        EnvironmentTokenProvider => "DATABRICKS_TOKEN",
        M2mTokenProvider => "OAuth M2M (DATABRICKS_CLIENT_ID + DATABRICKS_CLIENT_SECRET)",
        _ => "the registered token provider",
    };
}
