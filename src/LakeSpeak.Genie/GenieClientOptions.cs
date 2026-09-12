namespace LakeSpeak.Genie;

/// <summary>Configuration for <see cref="IGenieClient"/>.</summary>
public sealed class GenieClientOptions
{
    // CancellationTokenSource is the narrowest bound among the timer-backed polling operations.
    // Reject unsupported values while configuration is still actionable.
    internal static readonly TimeSpan MaximumTimerDuration =
        TimeSpan.FromMilliseconds(int.MaxValue);

    // Polly's timeout strategy requires a value strictly inside this range. RequestTimeout owns
    // the resilience pipeline's total deadline, so accept exactly the values that pipeline can use.
    internal static readonly TimeSpan MinimumRequestTimeout = TimeSpan.FromMilliseconds(10);
    internal static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromHours(24);

    /// <summary>Workspace base URL, for example <c>https://example.azuredatabricks.net</c>.</summary>
    public Uri? Host { get; set; }

    /// <summary>Databricks CLI profile to broker a token through. Ignored when a token provider is supplied directly.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// How long to wait for a message to reach a terminal state.
    /// </summary>
    /// <remarks>
    /// Ten minutes because a Genie question that starts a cold warehouse genuinely can take
    /// minutes, and a client that gives up at 60 seconds reports a timeout for a query that was
    /// about to succeed. Cancellation, not a short timeout, is the answer to impatience.
    /// </remarks>
    public TimeSpan PollingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>First polling interval. Databricks recommends 1–5 seconds.</summary>
    public TimeSpan InitialPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the backed-off polling interval.</summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-request HTTP timeout. Separate from <see cref="PollingTimeout"/>, which bounds the whole wait.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Page size for agent listing.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>The row count at which the client stops fetching <em>further</em> chunks.</summary>
    /// <remarks>
    /// <para>
    /// A chunked result is otherwise followed to its end, so without a bound the row count is
    /// whatever Databricks executed — and the rows are held in memory. When this limit stops the
    /// walk the result comes back flagged as truncated, exactly as an unreachable chunk does.
    /// </para>
    /// <para>
    /// It is deliberately <b>not</b> a hard cap on the rows returned. It is checked before
    /// requesting another chunk, so a single chunk larger than this limit is returned whole: those
    /// rows already arrived, and discarding data Databricks has already sent would be a worse
    /// trade than the memory. Size a host for the largest single chunk, not for this number.
    /// </para>
    /// </remarks>
    public int MaxResultRows { get; set; } = 100_000;

    internal void Validate()
    {
        if (Host is null)
        {
            throw new InvalidOperationException(
                "No Databricks host configured. Set GenieClientOptions.Host, the DATABRICKS_HOST " +
                "environment variable, or a profile in .databrickscfg.");
        }

        if (!Host.IsAbsoluteUri || Host.Scheme != Uri.UriSchemeHttps)
        {
            // A bearer token sent over http is a disclosed token, and the mistake is silent.
            throw new InvalidOperationException(
                $"Databricks host must be an absolute https URL. Got: {Host}");
        }

        ValidatePositiveDuration(PollingTimeout, nameof(PollingTimeout));
        ValidatePositiveDuration(InitialPollInterval, nameof(InitialPollInterval));
        ValidatePositiveDuration(MaxPollInterval, nameof(MaxPollInterval));
        ValidateRequestTimeout(RequestTimeout);

        if (InitialPollInterval > MaxPollInterval)
        {
            throw new InvalidOperationException(
                "InitialPollInterval must not exceed MaxPollInterval.");
        }

        if (MaxResultRows < 1)
        {
            // Zero would report every result as truncated while returning nothing, which reads
            // as a broken workspace rather than a misconfiguration.
            throw new InvalidOperationException("MaxResultRows must be at least 1.");
        }

        if (PageSize < 1)
        {
            throw new InvalidOperationException("PageSize must be at least 1.");
        }
    }

    internal static void ValidateWaitTimeout(TimeSpan timeout, string parameterName)
    {
        if (!IsSupportedDuration(timeout))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timeout,
                $"The timeout must be positive and no greater than {MaximumTimerDuration}.");
        }
    }

    private static void ValidatePositiveDuration(TimeSpan value, string propertyName)
    {
        if (!IsSupportedDuration(value))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be positive and no greater than {MaximumTimerDuration}.");
        }
    }

    private static void ValidateRequestTimeout(TimeSpan value)
    {
        if (value <= MinimumRequestTimeout || value >= MaximumRequestTimeout)
        {
            throw new InvalidOperationException(
                $"{nameof(RequestTimeout)} must be greater than {MinimumRequestTimeout} " +
                $"and less than {MaximumRequestTimeout}.");
        }
    }

    private static bool IsSupportedDuration(TimeSpan value) =>
        value > TimeSpan.Zero && value <= MaximumTimerDuration;
}
