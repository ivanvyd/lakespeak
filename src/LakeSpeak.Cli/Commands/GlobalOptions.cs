using System.CommandLine;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

// Recursive so they are accepted on every subcommand. Without this they parse only
// immediately after the executable name, which is not where anyone types them: the whole
// point of `--profile` is `lakespeak ask --profile prod "..."`.
internal static class GlobalOptions
{
    internal static readonly Option<string?> Profile =
        new("--profile", "-p")
        {
            Description = "Databricks CLI profile to authenticate with.",
            Recursive = true,
        };

    internal static readonly Option<string> Format =
        new("--format", "-f")
        {
            Description = "Output format: text, table, markdown, json, jsonl, csv.",
            Recursive = true,
            DefaultValueFactory = _ => "text",
        };

    internal static readonly Option<bool> Quiet =
        new("--quiet", "-q")
        {
            Description = "Suppress progress output on stderr.",
            Recursive = true,
        };

    internal static readonly Option<bool> Verbose =
        new("--verbose")
        {
            Description = "Print diagnostic detail to stderr. Credentials are always redacted.",
            Recursive = true,
        };

    /// <param name="parseResult">The parsed command line.</param>
    /// <param name="configuredDefault">
    /// `defaults.output` from the config file, used when the flag is absent. Passed in rather
    /// than read here so this stays a pure function of its inputs.
    /// </param>
    internal static OutputFormat ResolveFormat(ParseResult parseResult, string? configuredDefault = null)
    {
        // OptionResult.Implicit distinguishes the default value from an explicit format flag.
        // Comparing only the parsed string made a configured JSON default override an explicit
        // request for text.
        var formatResult = parseResult.GetResult(Format);
        var raw = formatResult is null || formatResult.Implicit
            ? configuredDefault ?? parseResult.GetValue(Format)
            : parseResult.GetValue(Format);

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "text";
        }

        if (!OutputFormatExtensions.TryParse(raw, out var format))
        {
            throw new CliUsageException(
                $"Unknown format '{raw}'. Valid values: text, table, markdown, json, jsonl, csv.");
        }

        return format;
    }
}

/// <summary>Argument checks shared by more than one command.</summary>
internal static class CliArguments
{
    /// <summary>
    /// Validates the <c>target</c> argument that <c>export</c> and <c>feedback</c> both take.
    /// Shared because both are documented as accepting more values later, and two copies of the
    /// accepted-value list is how one of them gets extended and the other quietly does not.
    /// </summary>
    internal static void RequireLastTarget(string? target)
    {
        if (!string.Equals(target, "last", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"Unknown target '{target}'. Only 'last' is supported in this version.");
        }
    }
}

/// <summary>A problem with what the user typed, as opposed to a failure talking to Databricks.</summary>
internal sealed class CliUsageException(string message) : Exception(message);
