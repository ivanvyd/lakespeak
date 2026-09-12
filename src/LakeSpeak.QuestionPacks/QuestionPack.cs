using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LakeSpeak.QuestionPacks;

public sealed record QuestionPack(
    string Name,
    string? Description,
    string Agent,
    string? Profile,
    IReadOnlyList<PackQuestion> Questions,
    PackOutput Output,
    PackBehavior Behavior)
{
    /// <summary>Directory the pack was loaded from. Output paths resolve against it, not the cwd.</summary>
    public string BaseDirectory { get; init; } = ".";
}

public sealed record PackQuestion(string Id, string? Title, string Ask, TimeSpan? Timeout);

public sealed record PackOutput(string Format, string? Path);

public sealed record PackBehavior(
    bool ContinueOnQuestionFailure,
    bool IncludeGeneratedSql,
    bool IncludeTimings,
    bool IncludeIdentifiers,
    TimeSpan Timeout);

public sealed class PackValidationException(IReadOnlyList<string> errors)
    : Exception("The Question Pack is not valid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Loads and validates a Question Pack.
/// </summary>
/// <remarks>
/// Validation is strict and happens before anything is executed. A pack is data: it names an
/// Agent and some questions, and it cannot run a command, read a file, or reach outside its own
/// directory to write one. Unknown keys are rejected rather than ignored, so a typo in
/// <c>continueOnQuestionFailure</c> fails loudly instead of silently changing behaviour.
/// </remarks>
public static partial class QuestionPackLoader
{
    private const int MaxQuestions = 50;
    private const int MaxDescriptionLength = 500;
    private const int MaxQuestionTitleLength = 200;
    private const int MaxQuestionLength = 4000;
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>The apiVersion every pack should declare. The domain is the project's own.</summary>
    public const string CurrentApiVersion = "lakespeak.net/v1alpha1";

    /// <summary>
    /// The apiVersion packs written before the project moved to lakespeak.net declare. Still
    /// accepted, and deliberately: the identifier is a name rather than a URL, nothing resolves
    /// it, and rejecting it would break every pack already committed to someone's repository to
    /// buy nothing. New packs get <see cref="CurrentApiVersion"/>; both mean the same schema.
    /// </summary>
    public const string LegacyApiVersion = "lakespeak.dev/v1alpha1";

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^([1-9][0-9]*)([smh])$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Duration();

    public static QuestionPack Load(string path)
    {
        var text = File.ReadAllText(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Parse(text, directory);
    }

    public static QuestionPack Parse(string yaml, string baseDirectory)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        RawPack? raw;
        try
        {
            raw = deserializer.Deserialize<RawPack>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PackValidationException([$"line {ex.Start.Line}: {ex.Message}"]);
        }

        if (raw is null)
        {
            throw new PackValidationException(["the file is empty"]);
        }

        var errors = new List<string>();

        if (raw.ApiVersion != CurrentApiVersion && raw.ApiVersion != LegacyApiVersion)
        {
            errors.Add($"apiVersion must be '{CurrentApiVersion}' (found '{raw.ApiVersion ?? "nothing"}')");
        }

        if (raw.Kind != "QuestionPack")
        {
            errors.Add($"kind must be 'QuestionPack' (found '{raw.Kind ?? "nothing"}')");
        }

        var name = raw.Metadata?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("metadata.name is required");
        }
        else if (!SafeName().IsMatch(name))
        {
            // The name reaches a filename, so anything else is a path-traversal vector.
            errors.Add($"metadata.name '{name}' must be lowercase kebab-case");
        }

        if (raw.Metadata?.Description is { } description
            && ExceedsLength(description, MaxDescriptionLength))
        {
            errors.Add($"metadata.description must be at most {MaxDescriptionLength} characters");
        }

        if (string.IsNullOrWhiteSpace(raw.Spec?.Agent))
        {
            errors.Add("spec.agent is required; a pack must say which Agent it runs against");
        }

        if (raw.Spec?.Profile is not null && string.IsNullOrWhiteSpace(raw.Spec.Profile))
        {
            errors.Add("spec.profile must not be empty when specified");
        }

        var questions = new List<PackQuestion>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        if (raw.Spec?.Questions is not { Count: > 0 } rawQuestions)
        {
            errors.Add("spec.questions must contain at least one question");
        }
        else
        {
            if (rawQuestions.Count > MaxQuestions)
            {
                errors.Add($"spec.questions has {rawQuestions.Count} entries; the maximum is {MaxQuestions}");
            }

            for (var index = 0; index < rawQuestions.Count; index++)
            {
                var q = rawQuestions[index];
                if (q is null)
                {
                    errors.Add($"question {index + 1} must be an object, not null");
                    continue;
                }

                var questionErrors = errors.Count;
                var id = q.Id;
                var questionLabel = string.IsNullOrWhiteSpace(id)
                    ? (index + 1).ToString(CultureInfo.InvariantCulture)
                    : id;
                if (string.IsNullOrWhiteSpace(id) || !SafeName().IsMatch(id))
                {
                    errors.Add($"question id '{id}' must be lowercase kebab-case");
                }
                else if (!seenIds.Add(id))
                {
                    // Duplicate ids would collide as report anchors and make results ambiguous.
                    errors.Add($"duplicate question id '{id}'");
                }

                if (string.IsNullOrWhiteSpace(q.Ask))
                {
                    errors.Add($"question '{questionLabel}' has an empty ask");
                }
                else if (ExceedsLength(q.Ask, MaxQuestionLength))
                {
                    errors.Add($"question '{questionLabel}' ask must be at most {MaxQuestionLength} characters");
                }

                if (q.Title is { } title && ExceedsLength(title, MaxQuestionTitleLength))
                {
                    errors.Add($"question '{questionLabel}' title must be at most {MaxQuestionTitleLength} characters");
                }

                var timeout = ParseDuration(q.Timeout, errors, $"question '{questionLabel}' timeout");
                if (errors.Count == questionErrors)
                {
                    questions.Add(new PackQuestion(id!, q.Title, q.Ask!.Trim(), timeout));
                }
            }
        }

        var outputPath = raw.Spec?.Output?.Path;
        if (outputPath is not null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                errors.Add("spec.output.path must not be empty when specified");
            }
            else
            {
                ValidateOutputPath(outputPath, baseDirectory, errors);
            }
        }

        var format = raw.Spec?.Output?.Format ?? "markdown";
        // Only markdown, because only markdown has a writer. Accepting "json" here and then
        // writing markdown anyway is worse than rejecting it: the pack author gets a file that
        // validated cleanly and is silently the wrong format.
        if (format is not "markdown")
        {
            errors.Add($"spec.output.format must be 'markdown' (found '{format}'). JSON reports are not implemented.");
        }

        var behavior = raw.Spec?.Behavior;
        var behaviorTimeout = ParseDuration(behavior?.Timeout, errors, "behavior.timeout")
            ?? TimeSpan.FromMinutes(10);

        if (errors.Count > 0)
        {
            throw new PackValidationException(errors);
        }

        return new QuestionPack(
            name!,
            raw.Metadata?.Description,
            raw.Spec!.Agent!,
            raw.Spec.Profile,
            questions,
            new PackOutput(format, outputPath),
            new PackBehavior(
                behavior?.ContinueOnQuestionFailure ?? true,
                behavior?.IncludeGeneratedSql ?? false,
                behavior?.IncludeTimings ?? true,
                behavior?.IncludeIdentifiers ?? false,
                behaviorTimeout))
        {
            BaseDirectory = baseDirectory,
        };
    }

    /// <summary>
    /// Rejects an output path that escapes the pack's own directory.
    /// </summary>
    /// <remarks>
    /// A pack can arrive from a pull request or a shared repository, so its output path is
    /// attacker-influenced. Without this, <c>../../.ssh/authorized_keys</c> is a valid target.
    /// </remarks>
    private static void ValidateOutputPath(string path, string baseDirectory, List<string> errors)
    {
        if (Path.IsPathRooted(path))
        {
            errors.Add($"spec.output.path '{path}' must be relative to the pack file");
            return;
        }

        string root;
        string target;
        try
        {
            root = Path.GetFullPath(baseDirectory);
            target = Path.GetFullPath(Path.Combine(root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"spec.output.path '{path}' is not a valid path: {ex.Message}");
            return;
        }

        if (!IsInside(target, root))
        {
            errors.Add($"spec.output.path '{path}' resolves outside the pack directory");
            return;
        }

        if (string.Equals(target, root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            errors.Add($"spec.output.path '{path}' must name a file inside the pack directory");
            return;
        }

        // The lexical check above is not enough on its own. A pack arrives as a YAML file inside
        // a directory the author controls, and that directory can contain a symlink or a Windows
        // junction. `link/report.md` then passes every string comparison while the write follows
        // the reparse point and lands anywhere the attacker chose — traversal without a single
        // `..`. Each component between the root and the target is therefore resolved.
        if (ContainsLink(target, root))
        {
            errors.Add(
                $"spec.output.path '{path}' passes through a symbolic link or junction, so its " +
                "destination cannot be verified safely");
        }
    }

    internal static bool IsInside(string target, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, comparison)
            || string.Equals(target, root, comparison);
    }

    internal static bool ContainsLink(string target, string root)
    {
        for (var current = target;
             current is not null && current.Length >= root.Length;
             current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            try
            {
                if (info.LinkTarget is not null
                    || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // An unreadable or dangling component cannot be established as an ordinary path.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? ParseDuration(string? value, List<string> errors, string context)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{context}: the duration must not be empty when specified");
            return null;
        }

        var match = Duration().Match(value);
        if (!match.Success)
        {
            errors.Add($"{context}: '{value}' is not a duration such as 90s, 5m or 1h");
            return null;
        }

        if (!ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount == 0)
        {
            errors.Add($"{context}: '{value}' must be a positive duration that fits in TimeSpan");
            return null;
        }

        var ticksPerUnit = match.Groups[2].Value switch
        {
            "s" => (ulong)TimeSpan.TicksPerSecond,
            "m" => (ulong)TimeSpan.TicksPerMinute,
            "h" => (ulong)TimeSpan.TicksPerHour,
            _ => 0UL,
        };
        if (ticksPerUnit == 0
            || amount > (ulong)long.MaxValue / ticksPerUnit
            || amount * ticksPerUnit > (ulong)MaximumTimeout.Ticks)
        {
            errors.Add(
                $"{context}: '{value}' is too large; the maximum supported timeout is " +
                "about 24.9 days");
            return null;
        }

        return new TimeSpan((long)(amount * ticksPerUnit));
    }

    private static bool ExceedsLength(string value, int maximum) =>
        value.EnumerateRunes().Take(maximum + 1).Count() > maximum;

    // Deserialization shapes. Separate from the validated model so a half-valid file never
    // reaches the runner.
    private sealed class RawPack
    {
        public string? ApiVersion { get; set; }
        public string? Kind { get; set; }
        public RawMetadata? Metadata { get; set; }
        public RawSpec? Spec { get; set; }
    }

    private sealed class RawMetadata
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private sealed class RawSpec
    {
        public string? Agent { get; set; }
        public string? Profile { get; set; }
        public List<RawQuestion?>? Questions { get; set; }
        public RawOutput? Output { get; set; }
        public RawBehavior? Behavior { get; set; }
    }

    private sealed class RawQuestion
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Ask { get; set; }
        public string? Timeout { get; set; }
    }

    private sealed class RawOutput
    {
        public string? Format { get; set; }
        public string? Path { get; set; }
    }

    private sealed class RawBehavior
    {
        public bool? ContinueOnQuestionFailure { get; set; }
        public bool? IncludeGeneratedSql { get; set; }
        public bool? IncludeTimings { get; set; }
        public bool? IncludeIdentifiers { get; set; }
        public string? Timeout { get; set; }
    }
}
