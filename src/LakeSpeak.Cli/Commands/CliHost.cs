using System.CommandLine;
using LakeSpeak.Application;
using LakeSpeak.Cli.Console;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.Genie.Authentication;
using LakeSpeak.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeSpeak.Cli.Commands;

/// <summary>Builds the services a command needs and maps failures onto exit codes.</summary>
internal sealed class CliHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string? _explicitProfile;
    private readonly Func<string?, Uri?> _resolveHost;

    private CliHost(
        ServiceProvider services,
        ConsoleOutput output,
        LakeSpeakConfig config,
        OutputFormat format,
        EffectiveWorkspace workspace,
        TimeSpan defaultTimeout,
        RecentConversation? recentConversation,
        string? explicitProfile,
        Func<string?, Uri?> resolveHost)
    {
        _services = services;
        Output = output;
        Config = config;
        Format = format;
        Workspace = workspace;
        DefaultTimeout = defaultTimeout;
        RecentConversation = recentConversation;
        _explicitProfile = explicitProfile;
        _resolveHost = resolveHost;
    }

    internal ConsoleOutput Output { get; }

    internal LakeSpeakConfig Config { get; }

    internal OutputFormat Format { get; }

    internal EffectiveWorkspace Workspace { get; }

    internal TimeSpan DefaultTimeout { get; }

    /// <summary>
    /// The same pointer snapshot used to select the client workspace. Commands must not reload
    /// the file and combine identifiers from a newer pointer with this host.
    /// </summary>
    internal RecentConversation? RecentConversation { get; }

    internal EffectiveWorkspace ResolveWorkspaceForAgentSwitch(string agentName) =>
        Config.Agents.TryGetValue(agentName, out var alias)
            && !string.IsNullOrWhiteSpace(alias.Profile)
                ? CliWorkspaceResolver.Resolve(
                    Config,
                    _explicitProfile,
                    CliProfileRequest.ForNewCommand(agentName),
                    recent: null,
                    _resolveHost)
                : Workspace;

    internal GenieAskOptions CreateAskOptions(Action<GenieMessageState> onStateChanged) =>
        new()
        {
            IncludeQueryResult = true,
            Timeout = DefaultTimeout,
            OnStateChanged = onStateChanged,
        };

    internal IGenieClient Client => _services.GetRequiredService<IGenieClient>();

    internal GenieClientOptions ClientOptions =>
        _services.GetRequiredService<IOptions<GenieClientOptions>>().Value;

    /// <summary>
    /// The token provider the client will actually use — the CLI broker, the environment, or one
    /// a host application registered. <c>auth check</c> must test this rather than construct its
    /// own, or it reports failure for a setup that works.
    /// </summary>
    internal IGenieTokenProvider TokenProvider => _services.GetRequiredService<IGenieTokenProvider>();

    /// <summary>
    /// The clock, so commands read time the same way the library does rather than reaching for
    /// <c>DateTimeOffset.UtcNow</c> — which nothing can fake in a test.
    /// </summary>
    internal TimeProvider Clock => _services.GetRequiredService<TimeProvider>();

    internal AgentResolver Resolver => new(Client, Config);

    internal TerminalRenderer Renderer => new(Output.Out, Config.Display.MaxRows);

    internal static CliHost Create(
        ParseResult parseResult,
        CliProfileRequest? profileRequest = null)
    {
        var config = LakeSpeakConfig.Load();
        profileRequest ??= CliProfileRequest.ForNewCommand();
        var recent = profileRequest.UseRecentConversation
            ? RecentConversation.Load()
            : null;

        return Create(
            parseResult,
            profileRequest,
            config,
            recent,
            DatabricksProfiles.ResolveHost);
    }

    /// <summary>
    /// Test seam for workspace routing. Supplying the config, pointer and host resolver keeps
    /// command tests isolated from the real home directory, credentials and network.
    /// </summary>
    internal static CliHost Create(
        ParseResult parseResult,
        CliProfileRequest profileRequest,
        LakeSpeakConfig config,
        RecentConversation? recent,
        Func<Uri?, string?, string?, Uri?> hostResolver,
        Action<IServiceCollection>? configureServices = null)
    {
        var format = GlobalOptions.ResolveFormat(parseResult, config.Defaults.Output);
        var quiet = parseResult.GetValue(GlobalOptions.Quiet);
        var output = new ConsoleOutput(format, quiet);

        var explicitProfile = parseResult.GetValue(GlobalOptions.Profile);
        Func<string?, Uri?> resolveHost = profile => hostResolver(null, profile, null);
        var workspace = CliWorkspaceResolver.Resolve(
            config,
            explicitProfile,
            profileRequest,
            recent,
            resolveHost);

        if (!ConfigDuration.TryParse(config.Defaults.Timeout, out var defaultTimeout))
        {
            // Load validates this. Keeping the guard here makes the test seam and any future
            // programmatic construction fail predictably too.
            throw new CliUsageException(
                $"defaults.timeout '{config.Defaults.Timeout}' must be a positive duration such as 90s, 5m, or 1h " +
                "and no longer than about 24.9 days.");
        }

        var services = new ServiceCollection();

        // Every record is scrubbed on its way out by RedactingStderrLoggerProvider, which is what
        // makes the --verbose help text's redaction promise true rather than aspirational.
        if (parseResult.GetValue(GlobalOptions.Verbose))
        {
            services.AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new RedactingStderrLoggerProvider()));
        }

        services.AddLakeSpeak(options =>
        {
            options.Profile = workspace.Profile;
            options.Host = workspace.Host;
        });
        configureServices?.Invoke(services);

        return new CliHost(
            services.BuildServiceProvider(),
            output,
            config,
            format,
            workspace,
            defaultTimeout,
            recent,
            explicitProfile,
            resolveHost);
    }

    /// <summary>
    /// Runs a command body, turning every expected failure into a message on stderr and an
    /// exit code, rather than a stack trace.
    /// </summary>
    internal static async Task<int> RunAsync(
        ParseResult parseResult,
        Func<CliHost, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken,
        CliProfileRequest? profileRequest = null,
        Func<ParseResult, CliProfileRequest?, CliHost>? hostFactory = null)
    {
        CliHost? host = null;
        try
        {
            host = hostFactory is null
                ? Create(parseResult, profileRequest)
                : hostFactory(parseResult, profileRequest);
            return await body(host, cancellationToken).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (GenieException ex)
        {
            ReportFailure(host, ex.Message);
            return ExitCode.From(ex.Kind);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is a normal way to end a long question, not an error to shout about.
            host?.Output.Status("Cancelled.");
            return ExitCode.Timeout;
        }
        catch (ArgumentException ex)
        {
            // A bad combination of arguments is the caller's mistake, so it exits as invalid
            // usage rather than as an unexpected failure a script would treat as a crash.
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (InvalidOperationException ex)
        {
            // Configuration problems surface as this: a malformed config file, or no host.
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (OptionsValidationException ex)
        {
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (IOException ex)
        {
            ReportFailure(host, ex.Message);
            return ExitCode.InvalidUsage;
        }
        finally
        {
            host?.Dispose();
        }
    }

    internal static void ReportFailure(
        CliHost? host,
        string message,
        TextWriter? fallback = null)
    {
        if (host is not null)
        {
            host.Output.Fail(message);
            return;
        }

        (fallback ?? System.Console.Error).WriteLine(
            $"error: {TerminalSafety.Sanitize(message)}");
    }

    public void Dispose() => _services.Dispose();
}
