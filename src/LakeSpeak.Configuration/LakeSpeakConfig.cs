using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LakeSpeak.Configuration;

/// <summary>
/// User configuration, read from <c>%APPDATA%\LakeSpeak\config.yaml</c> on Windows and
/// <c>~/.config/lakespeak/config.yaml</c> elsewhere.
/// </summary>
/// <remarks>
/// Holds preferences, Agent aliases and profile names. It deliberately holds no credentials, no
/// questions, no answers and no query results: a configuration file gets copied into dotfile
/// repositories and pasted into issues, so anything sensitive in it eventually escapes.
/// </remarks>
public sealed class LakeSpeakConfig
{
    public int Version { get; set; } = 1;

    public Defaults Defaults { get; set; } = new();

    /// <summary>Alias to Agent mapping, so scripts do not carry raw Agent ids.</summary>
    public Dictionary<string, AgentAlias> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DisplaySettings Display { get; set; } = new();

    public static string DefaultPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LakeSpeak",
                    "config.yaml");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
            return Path.Combine(root, "lakespeak", "config.yaml");
        }
    }

    public static LakeSpeakConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new LakeSpeakConfig();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            var loaded = deserializer.Deserialize<LakeSpeakConfig>(File.ReadAllText(path))
                ?? new LakeSpeakConfig();

            var errors = Validate(loaded);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{path} has invalid configuration:{Environment.NewLine}- " +
                    string.Join($"{Environment.NewLine}- ", errors));
            }

            // YamlDotNet constructs its own dictionary and assigns it over the field
            // initializer, discarding the OrdinalIgnoreCase comparer. Without rebuilding it,
            // an alias written `Finance:` would not match `--agent finance`, and resolution
            // would silently fall through to title matching — which can select a different
            // Agent entirely. That is the "answer against the wrong data" failure this
            // codebase exists to avoid.
            loaded.Agents = new Dictionary<string, AgentAlias>(
                loaded.Agents, StringComparer.OrdinalIgnoreCase);

            return loaded;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // Naming the file and the line is the difference between a two-second fix and a
            // confusing "no agents found" further down.
            throw new InvalidOperationException(
                $"{path} is not valid YAML (line {ex.Start.Line}): {ex.Message}", ex);
        }
    }

    private static List<string> Validate(LakeSpeakConfig config)
    {
        var errors = new List<string>();

        if (config.Version != 1)
        {
            errors.Add($"version must be 1 (found {config.Version}).");
        }

        if (config.Defaults is null)
        {
            errors.Add("defaults must be a mapping, not null.");
        }
        else
        {
            if (config.Defaults.Profile is not null
                && string.IsNullOrWhiteSpace(config.Defaults.Profile))
            {
                errors.Add("defaults.profile must not be empty when specified.");
            }

            if (config.Defaults.Agent is not null
                && string.IsNullOrWhiteSpace(config.Defaults.Agent))
            {
                errors.Add("defaults.agent must not be empty when specified.");
            }

            if (!SupportedOutputs.Contains(config.Defaults.Output, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"defaults.output '{config.Defaults.Output}' is not supported; use text, table, markdown, json, jsonl, or csv.");
            }

            if (!ConfigDuration.TryParse(config.Defaults.Timeout, out _))
            {
                errors.Add(
                    $"defaults.timeout '{config.Defaults.Timeout}' must be a positive duration such as 90s, 5m, or 1h " +
                    "and no longer than about 24.9 days.");
            }
        }

        if (config.Display is null)
        {
            errors.Add("display must be a mapping, not null.");
        }
        else if (config.Display.MaxRows <= 0)
        {
            errors.Add("display.maxRows must be greater than zero.");
        }

        if (config.Agents is null)
        {
            errors.Add("agents must be a mapping, not null.");
        }
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, alias) in config.Agents)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add("agent alias names must not be empty.");
                }
                else if (!names.Add(name))
                {
                    errors.Add($"agent alias '{name}' is duplicated with different casing.");
                }

                if (alias is null)
                {
                    errors.Add($"agent alias '{name}' must be a mapping, not null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(alias.Id))
                {
                    errors.Add($"agent alias '{name}' must define a non-empty id.");
                }

                if (alias.Profile is not null
                    && string.IsNullOrWhiteSpace(alias.Profile))
                {
                    errors.Add($"agent alias '{name}' profile must not be empty when specified.");
                }
            }
        }

        return errors;
    }

    private static readonly string[] SupportedOutputs =
        ["text", "table", "markdown", "json", "jsonl", "csv"];
}

public sealed class Defaults
{
    public string? Profile { get; set; }

    public string? Agent { get; set; }

    public string Output { get; set; } = "text";

    /// <summary>Overall wait for an answer, as a duration such as <c>10m</c>.</summary>
    public string Timeout { get; set; } = "10m";
}

public sealed class AgentAlias
{
    public string? Id { get; set; }

    public string? Profile { get; set; }
}

public sealed class DisplaySettings
{
    /// <summary>Rows shown in a terminal table. The full result is still exported in full.</summary>
    public int MaxRows { get; set; } = 50;

    public bool ShowSqlByDefault { get; set; }
}
