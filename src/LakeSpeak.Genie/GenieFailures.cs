using System.Text.RegularExpressions;

namespace LakeSpeak.Genie;

/// <summary>
/// Why a Genie operation failed, in terms a caller can branch on.
/// </summary>
/// <remarks>
/// This is the client's own closed set, not a mirror of any platform enum, so switching over it
/// exhaustively is safe and intended.
/// </remarks>
public enum GenieFailureKind
{
    Authentication,
    Authorization,
    AgentNotFound,
    ConversationNotFound,
    MessageFailed,
    MessageCancelled,
    PollingTimeout,
    RateLimited,
    QueryResultExpired,
    QueryExecutionFailed,
    MalformedResponse,

    /// <summary>A result shape this version cannot read, such as external links.</summary>
    UnsupportedResult,
    Network,
    Unexpected,
}

/// <summary>
/// A Genie operation that failed for a reason the caller may want to handle.
/// </summary>
/// <remarks>
/// Messages are built from redacted material only. An exception message routinely ends up in a
/// log aggregator, a CI transcript, or a bug report, so a token reaching one is a token
/// disclosed.
/// </remarks>
public sealed class GenieException : Exception
{
    public GenieFailureKind Kind { get; }
    public int? StatusCode { get; }
    public string? ErrorCode { get; }

    /// <summary>The last message observed before failing. Present for timeouts, where it is the only trace of progress.</summary>
    public GenieResponse? LastKnownResponse { get; }

    public GenieException(
        GenieFailureKind kind,
        string message,
        int? statusCode = null,
        string? errorCode = null,
        GenieResponse? lastKnownResponse = null,
        Exception? innerException = null)
        : base(DiagnosticRedaction.Scrub(message), innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        LastKnownResponse = lastKnownResponse;
    }

    /// <summary>
    /// Whether retrying the same call could plausibly succeed. Authorization failures are not
    /// retryable: retrying a 403 burns quota to produce the same 403.
    /// </summary>
    public bool IsRetryable => Kind is GenieFailureKind.RateLimited or GenieFailureKind.Network;

    /// <summary>Formats this exception and its retained cause without exposing credential-shaped data.</summary>
    public override string ToString() => DiagnosticRedaction.Scrub(base.ToString());
}

/// <summary>
/// Removes credential-shaped material from anything heading for a log, a terminal, or an
/// exception message.
/// </summary>
/// <remarks>
/// A denylist cannot be complete, so it is the last line rather than the only one: the client
/// also never puts a token in a URL, an argument vector, or a diagnostic field. This exists
/// because the cost of one leak is unrecoverable and the cost of over-scrubbing is a less
/// readable log line.
/// </remarks>
public static partial class DiagnosticRedaction
{
    public const string Placeholder = "[redacted]";

    [GeneratedRegex(@"\b(dapi[a-fA-F0-9]{32,})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DatabricksPat();

    // Three dot-separated base64url segments.
    [GeneratedRegex(@"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Jwt();

    // Two details carry this pattern, and both were found by a credential surviving it.
    //
    // The optional quote after the key name: in JSON the key is `"name":"value"`, so a pattern
    // jumping straight from the name to the separator never matches and the secret survives.
    //
    // The optional scheme word: `Authorization: Bearer <token>` is the single most common way a
    // credential is written down. Without consuming `Bearer`/`Basic` first, the value capture
    // stops at the space and redacts only the scheme — leaving the credential itself in the
    // output. That is precisely what happened, so the scheme is consumed and the token after it
    // is what gets taken.
    [GeneratedRegex(@"(?i)\b(authorization|bearer|x-databricks-[\w-]*token|access_token|refresh_token|client_secret|download_id_signature|statement_id_signature)\b[""']?\s*[:=]?\s*(?:(?:bearer|basic)\s+)?[""']?([^\s""',}]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedSecret();

    /// <summary>Replaces credential-shaped substrings with <see cref="Placeholder"/>.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var scrubbed = DatabricksPat().Replace(text, Placeholder);
        scrubbed = Jwt().Replace(scrubbed, Placeholder);
        scrubbed = NamedSecret().Replace(scrubbed, m => $"{m.Groups[1].Value}={Placeholder}");
        return scrubbed;
    }
}
