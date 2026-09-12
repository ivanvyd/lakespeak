using System.CommandLine;
using System.Text;
using LakeSpeak.Cli.Console;
using LakeSpeak.Configuration;
using LakeSpeak.Rendering;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class ExportCommand
{
    private static readonly Argument<string> Target =
        new("target")
        {
            Description = "Which result to export. Currently only 'last'.",
            DefaultValueFactory = _ => "last",
        };

    private static readonly Option<string?> Output =
        new("--output", "-o") { Description = "File to write. Defaults to stdout." };

    private static readonly Option<bool> Force =
        new("--force") { Description = "Overwrite the output file if it exists." };

    internal static Command Create()
    {
        var command = new Command("export", "Export the last query result without opening a chat session.")
        {
            Target,
            Output,
            Force,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(
                parseResult,
                (host, ct) => RunAsync(host, parseResult, ct),
                cancellationToken,
                ProfileRequest()));

        return command;
    }

    internal static CliProfileRequest ProfileRequest() =>
        CliProfileRequest.ForLastAnswer();

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        CliArguments.RequireLastTarget(parseResult.GetValue(Target));

        // The pointer records which profile the conversation lives in. Without consulting it,
        // this command would resolve the profile from the flag or config default and could
        // address a different workspace than the answer came from.
        var recent = host.RecentConversation
            ?? throw new CliUsageException("No previous answer to export. Run `lakespeak ask` first.");

        if (recent.AttachmentId is null
            || recent.AgentId is null
            || recent.ConversationId is null
            || recent.MessageId is null)
        {
            throw new CliUsageException(
                "The last answer had no query result to export. Only answers backed by a query can be exported.");
        }

        // The result is re-fetched rather than cached locally: keeping rows on disk between
        // commands would be a second copy of governed data with none of the governance, and
        // Databricks is already the system of record.
        var result = await host.Client.GetQueryResultAsync(
            recent.AgentId, recent.ConversationId, recent.MessageId, recent.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            // The cached result aged out. Re-running the attachment's query is the documented
            // recovery, and it is the right call here specifically because the user asked to
            // export: they want the rows, and re-executing costs warehouse time they have
            // implicitly agreed to. `ask` deliberately does not do this on their behalf.
            host.Output.Status("The cached result expired; re-running the query…");

            result = await host.Client.ReExecuteQueryAsync(
                recent.AgentId, recent.ConversationId, recent.MessageId, recent.AttachmentId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result is null)
        {
            throw new CliUsageException(
                "Databricks returned no result for that query, even after re-running it. Ask the question again.");
        }

        var path = parseResult.GetValue(Output);

        if (path is null)
        {
            await CsvWriter.WriteAsync(
                host.Output.ResultWriter,
                result,
                cancellationToken).ConfigureAwait(false);
            CliResultCompleteness.WarnIfIncomplete(host.Output, result);
            return ExitCode.Success;
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full) && !parseResult.GetValue(Force))
        {
            throw new CliUsageException($"{full} already exists. Pass --force to overwrite it.");
        }

        await WriteCsvFileAtomicAsync(
            full,
            result,
            parseResult.GetValue(Force),
            cancellationToken).ConfigureAwait(false);

        host.Output.Error.MarkupLine(
            $"[green]Wrote[/] {Markup.Escape(full)} [dim]({Wording.Count(result.RowCount, "row")}). " +
            "It contains governed data; look after it.[/]");

        CliResultCompleteness.WarnIfIncomplete(host.Output, result);

        return ExitCode.Success;
    }

    internal static async Task WriteCsvFileAtomicAsync(
        string fullPath,
        LakeSpeak.Genie.GenieQueryResult result,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new CliUsageException($"{fullPath} has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 65536);
                await using (writer.ConfigureAwait(false))
                {
                    await CsvWriter.WriteAsync(writer, result, cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The intended file was never installed if the write failed. A best-effort
                // cleanup must not hide the more useful write or cancellation error.
            }
        }
    }
}
