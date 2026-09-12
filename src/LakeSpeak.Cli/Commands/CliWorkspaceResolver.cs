using LakeSpeak.Configuration;

namespace LakeSpeak.Cli.Commands;

/// <summary>Inputs that can affect profile selection before a workspace client is created.</summary>
internal sealed record CliProfileRequest(
    string? AgentName,
    string? CommandProfile,
    bool UseRecentConversation)
{
    internal static CliProfileRequest ForNewCommand(
        string? agentName = null,
        string? commandProfile = null) =>
        new(agentName, commandProfile, UseRecentConversation: false);

    internal static CliProfileRequest ForLastAnswer() =>
        new(null, null, UseRecentConversation: true);
}

internal sealed record EffectiveWorkspace(string? Profile, Uri? Host, string Source);

internal static class CliWorkspaceResolver
{
    internal static EffectiveWorkspace Resolve(
        LakeSpeakConfig config,
        string? explicitProfile,
        CliProfileRequest request,
        RecentConversation? recent,
        Func<string?, Uri?> resolveHost)
    {
        if (request.UseRecentConversation)
        {
            return ResolveLastAnswer(config, explicitProfile, recent, resolveHost);
        }

        var agentName = request.AgentName ?? config.Defaults.Agent;
        var aliasProfile = agentName is { Length: > 0 }
            && config.Agents.TryGetValue(agentName, out var alias)
                ? alias.Profile
                : null;

        var profile = explicitProfile
            ?? request.CommandProfile
            ?? aliasProfile
            ?? config.Defaults.Profile;

        var source = explicitProfile is not null
            ? "--profile flag"
            : request.CommandProfile is not null
                ? "command profile"
                : aliasProfile is not null
                    ? $"agent alias '{agentName}'"
                    : config.Defaults.Profile is not null
                        ? "config defaults.profile"
                        : "authentication fallback";

        return new EffectiveWorkspace(profile, Normalize(resolveHost(profile)), source);
    }

    private static EffectiveWorkspace ResolveLastAnswer(
        LakeSpeakConfig config,
        string? explicitProfile,
        RecentConversation? recent,
        Func<string?, Uri?> resolveHost)
    {
        var savedHost = ParseSavedHost(recent?.WorkspaceHost);

        if (explicitProfile is not null)
        {
            var explicitHost = Normalize(resolveHost(explicitProfile));
            if (savedHost is not null)
            {
                if (explicitHost is null || !explicitHost.Equals(savedHost))
                {
                    throw new CliUsageException(
                        $"--profile '{explicitProfile}' resolves to a different workspace than the last answer. " +
                        "Run `lakespeak ask` in that workspace before exporting or rating it.");
                }
            }
            else if (recent?.Profile is { Length: > 0 } savedProfile
                && !string.Equals(savedProfile, explicitProfile, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliUsageException(
                    $"The last answer was created with profile '{savedProfile}', not '{explicitProfile}'. " +
                    "Run `lakespeak ask` with the intended profile first.");
            }

            return new EffectiveWorkspace(explicitProfile, explicitHost, "--profile flag");
        }

        if (recent is not null
            && (savedHost is not null || !string.IsNullOrWhiteSpace(recent.Profile)))
        {
            var profile = recent.Profile;
            var host = savedHost ?? Normalize(resolveHost(profile));
            return new EffectiveWorkspace(profile, host, "last answer");
        }

        // Legacy pointers may have no workspace identity. Retain the documented fallback for
        // those files only; every newly written pointer records the resolved workspace origin.
        return new EffectiveWorkspace(
            config.Defaults.Profile,
            Normalize(resolveHost(config.Defaults.Profile)),
            config.Defaults.Profile is null
                ? "authentication fallback (legacy pointer)"
                : "config defaults.profile (legacy pointer)");
    }

    internal static string? WorkspaceIdentity(Uri? host) =>
        Normalize(host)?.AbsoluteUri;

    internal static bool IsSameWorkspace(EffectiveWorkspace first, EffectiveWorkspace second)
    {
        var firstHost = Normalize(first.Host);
        var secondHost = Normalize(second.Host);
        if (firstHost is not null || secondHost is not null)
        {
            return Equals(firstHost, secondHost);
        }

        return string.Equals(
            first.Profile,
            second.Profile,
            StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? ParseSavedHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var host)
            || host.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(host.UserInfo))
        {
            throw new CliUsageException(
                "The stored workspace identity is invalid. Run `lakespeak ask` again.");
        }

        return Normalize(host);
    }

    private static Uri? Normalize(Uri? host)
    {
        if (host is null)
        {
            return null;
        }

        var builder = new UriBuilder(host.Scheme, host.Host)
        {
            Port = host.IsDefaultPort ? -1 : host.Port,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri;
    }
}
