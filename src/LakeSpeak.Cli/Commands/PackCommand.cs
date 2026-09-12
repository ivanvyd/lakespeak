using System.CommandLine;
using System.Reflection;
using System.Text;
using LakeSpeak.Cli.Console;
using LakeSpeak.QuestionPacks;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class PackCommand
{
    private static readonly Argument<string> Path =
        new("path") { Description = "Path to the Question Pack YAML file." };

    private static readonly Option<string?> Output =
        new("--output", "-o") { Description = "Write the report here instead of the pack's configured path." };

    private static readonly Option<bool> Force =
        new("--force") { Description = "Overwrite the output file if it already exists." };

    internal static Command Create()
    {
        var validate = new Command("validate", "Check a Question Pack without running it.")
        {
            Path,
        };
        validate.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) => Task.FromResult(Validate(host, parseResult)), cancellationToken));

        var run = new Command("run", "Run a Question Pack and write its report.")
        {
            Path,
            Output,
            Force,
        };
        run.SetAction((parseResult, cancellationToken) =>
            RunWithProfileAsync(parseResult, cancellationToken));

        var init = new Command("init", "Write a starter Question Pack.")
        {
            Path,
        };
        init.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) => Task.FromResult(Init(host, parseResult)), cancellationToken));

        var pack = new Command("pack", "Run repeatable, version-controlled sets of questions.");
        pack.Subcommands.Add(validate);
        pack.Subcommands.Add(run);
        pack.Subcommands.Add(init);
        return pack;
    }

    private static int Validate(CliHost host, ParseResult parseResult)
    {
        var path = RequirePath(parseResult);

        try
        {
            var pack = QuestionPackLoader.Load(path);
            host.Output.Error.MarkupLine(
                $"[green]OK[/] — [bold]{Markup.Escape(pack.Name)}[/]: " +
                $"{Wording.Count(pack.Questions.Count, "question")} against Agent '{Markup.Escape(pack.Agent)}'.");
            return ExitCode.Success;
        }
        catch (PackValidationException ex)
        {
            // Every problem at once. Reporting only the first turns fixing a pack into a
            // guessing game of repeated runs.
            host.Output.Fail($"{path} is not a valid Question Pack.");
            foreach (var error in ex.Errors)
            {
                host.Output.Error.MarkupLine($"  [red]•[/] {Markup.Escape(error)}");
            }

            return ExitCode.InvalidUsage;
        }
    }

    private static async Task<int> RunWithProfileAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        QuestionPack pack;

        try
        {
            pack = QuestionPackLoader.Load(RequirePath(parseResult));
        }
        catch (PackValidationException ex)
        {
            return await CliHost.RunAsync(
                parseResult,
                (host, _) =>
                {
                    host.Output.Fail(ex.Message);
                    return Task.FromResult(ExitCode.InvalidUsage);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return await CliHost.RunAsync(
                parseResult,
                (_, _) => Task.FromException<int>(ex),
                cancellationToken).ConfigureAwait(false);
        }

        return await CliHost.RunAsync(
            parseResult,
            (host, ct) => RunAsync(host, parseResult, pack, ct),
            cancellationToken,
            CliProfileRequest.ForNewCommand(pack.Agent, pack.Profile)).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        CliHost host,
        ParseResult parseResult,
        QuestionPack pack,
        CancellationToken cancellationToken)
    {
        var explicitTarget = parseResult.GetValue(Output);
        var target = explicitTarget ?? pack.Output.Path;
        PackOutputDestination? destination = null;
        if (target is not null)
        {
            try
            {
                destination = PackOutputDestination.Preflight(
                    pack.BaseDirectory,
                    target,
                    isPackPath: explicitTarget is null,
                    overwrite: parseResult.GetValue(Force));
            }
            catch (PackOutputException ex)
            {
                throw new CliUsageException(ex.Message);
            }
        }
        using var destinationLease = destination;

        var resolution = await host.Resolver.ResolveAsync(pack.Agent, cancellationToken).ConfigureAwait(false);
        if (resolution.Agent is null)
        {
            // Automation must never prompt. A pack run from cron that stops on a hidden
            // selector looks like a hang, not a failure.
            throw new CliUsageException(resolution.IsAmbiguous
                ? $"Agent '{pack.Agent}' is ambiguous; use the Agent id in the pack."
                : $"No Genie Agent matches '{pack.Agent}'.");
        }

        var runner = new PackRunner(host.Client);
        var progress = new Progress<PackQuestion>(q =>
            host.Output.Status($"Asking: {q.Title ?? q.Id}…"));

        using var sectionStream = CreateSectionSpool();
        using var sectionWriter = new StreamWriter(
            sectionStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 65536,
            leaveOpen: true);

        var result = await runner.RunStreamingAsync(
            pack,
            resolution.Agent.AgentId,
            resolution.Agent.Title,
            async (outcome, ct) =>
            {
                await PackReportWriter.WriteSectionAsync(
                    sectionWriter, outcome, pack, ct).ConfigureAwait(false);
                await sectionWriter.FlushAsync(ct).ConfigureAwait(false);
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var header = PackReportWriter.WriteHeader(result, version);
        await sectionWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        sectionStream.Position = 0;
        using var sectionReader = new StreamReader(
            sectionStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536,
            leaveOpen: true);

        try
        {
            if (destination is null)
            {
                await host.Output.ResultWriter.WriteAsync(
                    header.AsMemory(), cancellationToken).ConfigureAwait(false);
                await CopyAsync(
                    sectionReader, host.Output.ResultWriter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await destination.WriteAtomicAsync(
                    async (writer, ct) =>
                    {
                        await writer.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
                        await CopyAsync(sectionReader, writer, ct).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);

                host.Output.Error.MarkupLine(
                    $"[green]Wrote[/] {Markup.Escape(destination.FullPath)}");
            }
        }
        catch (IOException ex)
        {
            throw new CliUsageException($"Could not write the pack report: {ex.Message}");
        }

        if (result.AnyFailed)
        {
            host.Output.Warn(
                $"{result.FailureCount} of {result.Outcomes.Count} questions failed; the report says which.");
            return ExitCode.PartialPackFailure;
        }

        return ExitCode.Success;
    }

    private static FileStream CreateSectionSpool()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"lakespeak-pack-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    private static async Task CopyAsync(
        TextReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var buffer = new char[65536];
        int read;
        while ((read = await reader.ReadAsync(
            buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await writer.WriteAsync(
                buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int Init(CliHost host, ParseResult parseResult)
    {
        var path = RequirePath(parseResult);
        if (File.Exists(path))
        {
            throw new CliUsageException($"{path} already exists.");
        }

        File.WriteAllText(path, Template);
        host.Output.Error.MarkupLine($"[green]Wrote[/] {Markup.Escape(path)}");
        return ExitCode.Success;
    }

    private static string RequirePath(ParseResult parseResult) =>
        parseResult.GetValue(Path) ?? throw new CliUsageException("A pack file path is required.");

    private const string Template =
        """
        apiVersion: lakespeak.net/v1alpha1
        kind: QuestionPack

        metadata:
          name: daily-platform-brief
          description: Daily summary of Databricks platform health

        spec:
          # Agent id, exact title, or an alias from your LakeSpeak config.
          agent: platform-operations

          questions:
            - id: failed-jobs
              title: Failed production jobs
              ask: >
                Which production jobs failed during the last 24 hours?
                Include job name, failure time, and latest error category.

            - id: expensive-queries
              title: Most expensive queries
              ask: Which queries consumed the most compute yesterday?

          output:
            format: markdown
            path: reports/daily-platform-brief.md

          behavior:
            # Finish the run and report failures in place, rather than stopping at the first one.
            continueOnQuestionFailure: true
            includeGeneratedSql: false
            includeTimings: true
            # Conversation and message ids identify a conversation containing governed data,
            # so they are off unless you want the report to be traceable in Databricks.
            includeIdentifiers: false

        """;
}
