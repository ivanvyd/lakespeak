using LakeSpeak.Cli.Console;
using LakeSpeak.Genie;

namespace LakeSpeak.Cli.Commands;

internal static class CliResultCompleteness
{
    private const string IncompleteMessage =
        "This result is incomplete — Databricks truncated it, or it continues beyond the " +
        "rows this version reads. Narrow the question to retrieve everything.";

    internal static void WarnIfIncomplete(ConsoleOutput output, GenieQueryResult? result)
    {
        if (result?.IsTruncated == true)
        {
            // A data-loss warning is never progress output. It remains visible in quiet mode and
            // stays on stderr so JSONL/CSV/stdout exports remain parseable.
            output.Warn(IncompleteMessage);
        }
    }
}
