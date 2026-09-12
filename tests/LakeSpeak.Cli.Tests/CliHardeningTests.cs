using System.Text;
using System.Text.Json;
using LakeSpeak.Cli.Commands;
using LakeSpeak.Cli.Console;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.Genie.Authentication;
using LakeSpeak.QuestionPacks;
using LakeSpeak.Rendering;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;
using Spectre.Console.Testing;

namespace LakeSpeak.Cli.Tests;

public sealed class CliHardeningTests
{
    [Fact]
    public void An_explicit_text_format_wins_over_the_configured_default()
    {
        var parseResult = Program.CreateRootCommand().Parse(
            ["ask", "--agent", "sales", "--format", "text", "question"]);

        GlobalOptions.ResolveFormat(parseResult, "json").ShouldBe(OutputFormat.Text);
    }

    [Fact]
    public void A_null_defaults_section_is_reported_as_invalid_configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, "version: 1\ndefaults:\n");

            var error = Should.Throw<InvalidOperationException>(() => LakeSpeakConfig.Load(path));

            error.Message.ShouldContain("defaults");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_absent_format_uses_the_configured_default()
    {
        var parseResult = Program.CreateRootCommand().Parse(
            ["ask", "--agent", "sales", "question"]);

        GlobalOptions.ResolveFormat(parseResult, "json").ShouldBe(OutputFormat.Json);
    }

    [Fact]
    public void A_new_ask_uses_its_alias_profile_and_ignores_the_last_conversation()
    {
        var config = Config();
        config.Defaults.Profile = "default";
        config.Defaults.Timeout = "90s";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };
        var recent = new RecentConversation
        {
            Profile = "recent",
            WorkspaceHost = "not a valid workspace",
        };
        var parseResult = Program.CreateRootCommand().Parse(
            ["ask", "--agent", "sales", "question"]);

        using var host = CliHost.Create(
            parseResult,
            AskCommand.ProfileRequest(parseResult),
            config,
            recent,
            ResolveHost);

        host.Workspace.Profile.ShouldBe("alias");
        host.Workspace.Host.ShouldBe(new Uri("https://alias.example/"));
        host.ClientOptions.Profile.ShouldBe("alias");
        host.ClientOptions.Host.ShouldBe(new Uri("https://alias.example/"));
        host.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(90));
        host.CreateAskOptions(_ => { }).Timeout.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Chat_uses_the_default_agents_alias_profile()
    {
        var config = Config();
        config.Defaults.Agent = "sales";
        config.Defaults.Profile = "default";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };
        var parseResult = Program.CreateRootCommand().Parse(["chat"]);

        using var host = CliHost.Create(
            parseResult,
            ChatCommand.ProfileRequest(parseResult),
            config,
            recent: null,
            ResolveHost);

        host.Workspace.Profile.ShouldBe("alias");
        host.ClientOptions.Profile.ShouldBe("alias");
    }

    [Fact]
    public void An_explicit_profile_wins_over_pack_alias_and_default_profiles()
    {
        var config = Config();
        config.Defaults.Profile = "default";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };

        var workspace = CliWorkspaceResolver.Resolve(
            config,
            "explicit",
            CliProfileRequest.ForNewCommand("sales", "pack"),
            recent: null,
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("explicit");
        workspace.Host.ShouldBe(new Uri("https://explicit.example/"));
    }

    [Fact]
    public void A_pack_profile_wins_over_its_agent_alias_and_the_default()
    {
        var config = Config();
        config.Defaults.Profile = "default";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };

        var workspace = CliWorkspaceResolver.Resolve(
            config,
            explicitProfile: null,
            CliProfileRequest.ForNewCommand("sales", "pack"),
            recent: null,
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("pack");
        workspace.Host.ShouldBe(new Uri("https://pack.example/"));
    }

    [Fact]
    public void No_selected_profile_preserves_the_authentication_layer_fallback()
    {
        var workspace = CliWorkspaceResolver.Resolve(
            Config(),
            explicitProfile: null,
            CliProfileRequest.ForNewCommand(),
            new RecentConversation
            {
                Profile = "recent",
                WorkspaceHost = "https://recent.example/",
            },
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBeNull();
        workspace.Host.ShouldBe(new Uri("https://environment.example/"));
        workspace.Source.ShouldContain("authentication fallback");
    }

    [Fact]
    public void Export_uses_the_saved_workspace_after_the_config_default_changes()
    {
        var config = Config();
        config.Defaults.Profile = "changed-default";
        var recent = new RecentConversation
        {
            Profile = "original",
            WorkspaceHost = "https://original.example/some/path?ignored=true",
        };

        var workspace = CliWorkspaceResolver.Resolve(
            config,
            explicitProfile: null,
            ExportCommand.ProfileRequest(),
            recent,
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("original");
        workspace.Host.ShouldBe(new Uri("https://original.example/"));
        workspace.Source.ShouldBe("last answer");
    }

    [Fact]
    public void Export_rejects_an_explicit_profile_for_another_workspace()
    {
        var recent = new RecentConversation
        {
            Profile = "original",
            WorkspaceHost = "https://original.example/",
        };

        var error = Should.Throw<CliUsageException>(() => CliWorkspaceResolver.Resolve(
            Config(),
            "other",
            ExportCommand.ProfileRequest(),
            recent,
            profile => ResolveHost(null, profile, null)));

        error.Message.ShouldContain("different workspace");
    }

    [Fact]
    public void Export_allows_an_explicit_credential_profile_for_the_same_workspace()
    {
        var recent = new RecentConversation
        {
            Profile = "original",
            WorkspaceHost = "https://original.example/",
        };

        var workspace = CliWorkspaceResolver.Resolve(
            Config(),
            "alternate-original",
            FeedbackCommand.ProfileRequest(),
            recent,
            profile => profile == "alternate-original"
                ? new Uri("https://original.example/")
                : ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("alternate-original");
        workspace.Host.ShouldBe(new Uri("https://original.example/"));
    }

    [Fact]
    public void A_legacy_recent_pointer_falls_back_to_its_saved_profile()
    {
        var recent = new RecentConversation { Profile = "original" };

        var workspace = CliWorkspaceResolver.Resolve(
            Config(),
            explicitProfile: null,
            ExportCommand.ProfileRequest(),
            recent,
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("original");
        workspace.Host.ShouldBe(new Uri("https://original.example/"));
    }

    [Fact]
    public void A_legacy_pointer_without_workspace_identity_uses_the_configured_default()
    {
        var config = Config();
        config.Defaults.Profile = "default";

        var workspace = CliWorkspaceResolver.Resolve(
            config,
            explicitProfile: null,
            ExportCommand.ProfileRequest(),
            new RecentConversation(),
            profile => ResolveHost(null, profile, null));

        workspace.Profile.ShouldBe("default");
        workspace.Host.ShouldBe(new Uri("https://default.example/"));
        workspace.Source.ShouldContain("legacy pointer");
    }

    [Fact]
    public void Chat_refuses_an_agent_alias_from_another_workspace()
    {
        var config = Config();
        config.Defaults.Profile = "default";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };
        var parseResult = Program.CreateRootCommand().Parse(["chat"]);

        using var host = CliHost.Create(
            parseResult,
            ChatCommand.ProfileRequest(parseResult),
            config,
            recent: null,
            ResolveHost);

        ChatSession.RequiresWorkspaceSwitch(host, "sales").ShouldBeTrue();
    }

    [Fact]
    public void Chat_keeps_an_explicit_profile_when_switching_agent_aliases()
    {
        var config = Config();
        config.Defaults.Profile = "default";
        config.Agents["sales"] = new AgentAlias { Id = "agent-sales", Profile = "alias" };
        var parseResult = Program.CreateRootCommand().Parse(["--profile", "default", "chat"]);

        using var host = CliHost.Create(
            parseResult,
            ChatCommand.ProfileRequest(parseResult),
            config,
            recent: null,
            ResolveHost);

        ChatSession.RequiresWorkspaceSwitch(host, "sales").ShouldBeFalse();
    }

    [Fact]
    public void Last_answer_commands_keep_the_pointer_snapshot_used_for_workspace_selection()
    {
        var recent = new RecentConversation
        {
            Profile = "original",
            WorkspaceHost = "https://original.example/",
            AgentId = "agent",
            ConversationId = "conversation",
            MessageId = "message",
        };
        var parseResult = Program.CreateRootCommand().Parse(["export", "last"]);

        using var host = CliHost.Create(
            parseResult,
            ExportCommand.ProfileRequest(),
            Config(),
            recent,
            ResolveHost);

        host.RecentConversation.ShouldBeSameAs(recent);
    }

    [Fact]
    public void A_registered_token_provider_is_preserved()
    {
        var provider = Substitute.For<IGenieTokenProvider>();
        var parseResult = Program.CreateRootCommand().Parse(
            ["ask", "--agent", "sales", "question"]);

        using var host = CliHost.Create(
            parseResult,
            AskCommand.ProfileRequest(parseResult),
            Config(),
            recent: null,
            ResolveHost,
            services => services.AddSingleton(provider));

        host.TokenProvider.ShouldBeSameAs(provider);
    }

    [Fact]
    public void Saved_workspace_identity_contains_only_the_origin()
    {
        var saved = CliWorkspaceResolver.WorkspaceIdentity(
            new Uri("https://person:secret@workspace.example/path?token=sensitive#fragment"));

        saved.ShouldBe("https://workspace.example/");
    }

    [Fact]
    public void No_color_keeps_a_real_terminal_interactive_but_removes_decoration()
    {
        var capabilities = ConsoleOutput.DetermineCapabilities(
            OutputFormat.Text,
            outputRedirected: false,
            inputRedirected: false,
            noColor: true);

        capabilities.IsInteractive.ShouldBeTrue();
        capabilities.UsesDecoration.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OutputFormat.Text, true, false)]
    [InlineData(OutputFormat.Text, false, true)]
    [InlineData(OutputFormat.Json, false, false)]
    public void Redirected_or_machine_chat_is_noninteractive(
        OutputFormat format,
        bool outputRedirected,
        bool inputRedirected)
    {
        var capabilities = ConsoleOutput.DetermineCapabilities(
            format,
            outputRedirected,
            inputRedirected,
            noColor: false);

        capabilities.IsInteractive.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OutputFormat.Json, "[]")]
    [InlineData(OutputFormat.Csv, "id,title")]
    [InlineData(OutputFormat.Jsonl, "")]
    public void An_empty_agent_listing_remains_valid_machine_output(
        OutputFormat format,
        string expected)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var output = CapturedOutput(format, stdout, stderr);

        AgentsCommand.WriteListing(
            output,
            new TerminalRenderer(output.Out),
            format,
            []);

        stdout.ToString().Trim().ShouldBe(expected);
        stderr.ToString().ShouldContain("No Genie Agents");
        if (format == OutputFormat.Json)
        {
            JsonDocument.Parse(stdout.ToString()).RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        }
    }

    [Fact]
    public void Duplicate_chat_titles_keep_their_agent_identity()
    {
        var choices = ChatCommand.BuildAgentChoices(
            [
                new GenieAgent("agent-a", "Finance"),
                new GenieAgent("agent-b", "Finance"),
            ]);

        choices[0].Agent.AgentId.ShouldBe("agent-a");
        choices[0].Label.ShouldContain("agent-a");
        choices[1].Agent.AgentId.ShouldBe("agent-b");
        choices[1].Label.ShouldContain("agent-b");
    }

    [Fact]
    public void Keyboard_selection_returns_the_second_duplicate_agent()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var choices = ChatCommand.BuildAgentChoices(
            [
                new GenieAgent("agent-a", "Finance"),
                new GenieAgent("agent-b", "Finance"),
            ]);

        var selected = console.Prompt(ChatCommand.CreateAgentPrompt(choices));

        selected.Agent.AgentId.ShouldBe("agent-b");
    }

    [Fact]
    public void An_incomplete_result_warns_on_stderr_even_in_quiet_machine_mode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var output = CapturedOutput(OutputFormat.Jsonl, stdout, stderr, quiet: true);
        var result = new GenieQueryResult([], [], IsTruncated: true, TotalRowCount: 100);

        CliResultCompleteness.WarnIfIncomplete(output, result);

        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain("incomplete");
    }

    [Fact]
    public async Task Ask_jsonl_reaches_stdout_one_row_at_a_time_and_honors_cancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var stdout = new CancelAfterFirstLineWriter(cancellation);
        var output = new ConsoleOutput(
            OutputFormat.Jsonl,
            quiet: false,
            stdout,
            TextWriter.Null,
            outputRedirected: true,
            inputRedirected: true,
            noColor: true);
        var response = new GenieResponse(
            "agent",
            "conversation",
            "message",
            GenieMessageState.Completed,
            "answer",
            null,
            new GenieQueryResult(
                [new GenieColumn("value", "STRING", "STRING")],
                [["one"], ["two"]],
                IsTruncated: false,
                TotalRowCount: 2),
            [],
            new GenieResponseMetadata(TimeSpan.Zero, 1));

        var act = () => AskCommand.WriteAsync(
            output,
            new TerminalRenderer(output.Out),
            OutputFormat.Jsonl,
            response,
            new GenieAgent("agent", "Agent"),
            showSql: false,
            cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
        stdout.Lines.ShouldBe(["{\"value\":\"one\"}"]);
    }

    [Fact]
    public async Task Cancelled_export_preserves_the_existing_file()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"lakespeak-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "result.csv");
        await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        try
        {
            var result = new GenieQueryResult(
                [new GenieColumn("value", "STRING", "STRING")],
                [["replacement"]],
                IsTruncated: false,
                TotalRowCount: 1);

            await Should.ThrowAsync<OperationCanceledException>(() =>
                ExportCommand.WriteCsvFileAtomicAsync(path, result, overwrite: true, cancellation.Token));

            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("existing");
            Directory.GetFiles(directory).ShouldBe([path]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_complete_result_does_not_warn()
    {
        var stderr = new StringWriter();
        var output = CapturedOutput(OutputFormat.Csv, new StringWriter(), stderr);
        var result = new GenieQueryResult([], [], IsTruncated: false, TotalRowCount: 0);

        CliResultCompleteness.WarnIfIncomplete(output, result);

        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void A_startup_failure_is_visible_and_terminal_control_is_sanitized()
    {
        var stderr = new StringWriter();

        CliHost.ReportFailure(null, "bad \u001b[2J config", stderr);

        stderr.ToString().ShouldContain("error:");
        stderr.ToString().ShouldContain("bad");
        stderr.ToString().ShouldNotContain("\u001b[2J");
    }

    [Fact]
    public void Recent_state_round_trips_the_effective_profile_and_workspace_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-recent-{Guid.NewGuid():N}.yaml");
        try
        {
            new RecentConversation
            {
                Profile = "workspace-profile",
                WorkspaceHost = "https://workspace.example/",
                AgentId = "agent",
                ConversationId = "conversation",
                MessageId = "message",
            }.Save(path);

            var loaded = RecentConversation.Load(path);

            loaded.ShouldNotBeNull();
            loaded!.Profile.ShouldBe("workspace-profile");
            loaded.WorkspaceHost.ShouldBe("https://workspace.example/");
            var yaml = File.ReadAllText(path);
            yaml.ShouldNotContain("token");
            yaml.ShouldNotContain("secret");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_preexisting_pack_output_fails_before_any_client_call()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"lakespeak-pack-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "report.md");
        await File.WriteAllTextAsync(outputPath, "existing", TestContext.Current.CancellationToken);

        try
        {
            var pack = new QuestionPack(
                "preflight",
                null,
                "sales",
                "pack",
                [new PackQuestion("one", null, "question", null)],
                new PackOutput("markdown", null),
                new PackBehavior(true, false, true, false, TimeSpan.FromMinutes(1)))
            {
                BaseDirectory = directory,
            };
            var parseResult = Program.CreateRootCommand().Parse(
                ["pack", "run", "unused.yaml", "--output", outputPath]);
            var client = Substitute.For<IGenieClient>();
            var config = Config();

            var exitCode = await CliHost.RunAsync(
                parseResult,
                (host, cancellationToken) =>
                    PackCommand.RunAsync(host, parseResult, pack, cancellationToken),
                TestContext.Current.CancellationToken,
                CliProfileRequest.ForNewCommand(pack.Agent, pack.Profile),
                (parsed, request) => CliHost.Create(
                    parsed,
                    request!,
                    config,
                    recent: null,
                    ResolveHost,
                    services => services.AddSingleton(client)));

            exitCode.ShouldBe(ExitCode.InvalidUsage);
            client.ReceivedCalls().ShouldBeEmpty();
            (await File.ReadAllTextAsync(
                outputPath, TestContext.Current.CancellationToken)).ShouldBe("existing");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("version: 2", "version must be 1")]
    [InlineData("version: 1\ndefaults:\n  output: yaml", "defaults.output")]
    [InlineData("version: 1\ndefaults:\n  timeout: soon", "defaults.timeout")]
    [InlineData("version: 1\ndefaults:\n  timeout: +1s", "defaults.timeout")]
    [InlineData("version: 1\ndefaults:\n  timeout: 0s", "defaults.timeout")]
    [InlineData("version: 1\ndefaults:\n  timeout: 2147484s", "defaults.timeout")]
    [InlineData("version: 1\ndefaults:\n  timeout: 9223372036854775807h", "defaults.timeout")]
    [InlineData("version: 1\ndisplay:", "display")]
    [InlineData("version: 1\nagents:\n  sales:", "agent alias 'sales'")]
    public void Invalid_configuration_values_are_reported(string yaml, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, yaml);

            var error = Should.Throw<InvalidOperationException>(() => LakeSpeakConfig.Load(path));

            error.Message.ShouldContain(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Largest_whole_second_configuration_timeout_is_accepted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, "version: 1\ndefaults:\n  timeout: 2147483s");

            LakeSpeakConfig.Load(path).Defaults.Timeout.ShouldBe("2147483s");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Independent_configuration_errors_are_reported_together()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(
                path,
                "version: 2\ndefaults:\ndisplay:\nagents:\n  sales:\n");

            var error = Should.Throw<InvalidOperationException>(() => LakeSpeakConfig.Load(path));

            error.Message.ShouldContain("version");
            error.Message.ShouldContain("defaults");
            error.Message.ShouldContain("display");
            error.Message.ShouldContain("sales");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Independent_agent_alias_errors_are_reported_together()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lakespeak-config-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(
                path,
                "version: 1\nagents:\n  sales:\n    id: ''\n    profile: '   '");

            var error = Should.Throw<InvalidOperationException>(() => LakeSpeakConfig.Load(path));

            error.Message.ShouldContain("non-empty id");
            error.Message.ShouldContain("profile must not be empty");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LakeSpeakConfig Config() =>
        new()
        {
            Defaults = new Defaults
            {
                Output = "text",
                Timeout = "10m",
            },
        };

    private static Uri? ResolveHost(Uri? explicitHost, string? profile, string? path)
    {
        if (explicitHost is not null)
        {
            return explicitHost;
        }

        return profile switch
        {
            "explicit" => new Uri("https://explicit.example/"),
            "pack" => new Uri("https://pack.example/"),
            "alias" => new Uri("https://alias.example/"),
            "default" => new Uri("https://default.example/"),
            "changed-default" => new Uri("https://changed-default.example/"),
            "original" => new Uri("https://original.example/"),
            "other" => new Uri("https://other.example/"),
            _ => new Uri("https://environment.example/"),
        };
    }

    private static ConsoleOutput CapturedOutput(
        OutputFormat format,
        StringWriter stdout,
        StringWriter stderr,
        bool quiet = false) =>
        new(
            format,
            quiet,
            stdout,
            stderr,
            outputRedirected: true,
            inputRedirected: true,
            noColor: true);

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation) : TextWriter
    {
        private readonly StringBuilder _current = new();

        internal List<string> Lines { get; } = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current.Append(buffer.Span);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current.Append(buffer.Span);
            Lines.Add(_current.ToString());
            _current.Clear();
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }
}
