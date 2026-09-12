using System.Net.Http;
using LakeSpeak.Genie.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace LakeSpeak.Genie;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGenieClient"/> with a typed <see cref="HttpClient"/>, bearer-token
    /// authentication and the standard resilience pipeline.
    /// </summary>
    /// <remarks>
    /// If no <see cref="IGenieTokenProvider"/> has been registered, the Databricks CLI broker is
    /// used. Register your own before calling this to override that.
    /// </remarks>
    public static IServiceCollection AddLakeSpeak(
        this IServiceCollection services,
        Action<GenieClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GenieClientOptions>()
            .Configure(options =>
            {
                configure?.Invoke(options);

                // Fill the host from the environment or .databrickscfg only if the caller did
                // not set one, so an explicit value always wins.
                options.Host = DatabricksProfiles.ResolveHost(options.Host, options.Profile);
            })
            .Validate(
                options => options.Host is not null,
                "No Databricks host configured. Set GenieClientOptions.Host, DATABRICKS_HOST, or a profile in .databrickscfg.");

        services.TryAddSingleton<IGenieTokenProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GenieClientOptions>>().Value;

            // PAT wins when set. It is the only path that works without configuration and is
            // documented as the local-debugging option, so the rule of least surprise says it
            // stays the first check.
            if (Environment.GetEnvironmentVariable(EnvironmentTokenProvider.TokenVariable) is { Length: > 0 })
            {
                return new EnvironmentTokenProvider();
            }

            // OAuth M2M is the unattended path. A service principal's client_id + client_secret
            // rotate via the Databricks token endpoint, so a Question Pack on a schedule does
            // not have to hold a long-lived personal credential. The two variables have to be
            // set together: a half-set pair is a misconfiguration, not a fallback.
            var clientId = Environment.GetEnvironmentVariable(M2mTokenProvider.ClientIdVariable);
            var clientSecret = Environment.GetEnvironmentVariable(M2mTokenProvider.ClientSecretVariable);
            if (!string.IsNullOrEmpty(clientId) || !string.IsNullOrEmpty(clientSecret))
            {
                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    throw new InvalidOperationException(
                        $"{M2mTokenProvider.ClientIdVariable} and {M2mTokenProvider.ClientSecretVariable} " +
                        "must be set together. Setting only one causes an OAuth failure that reads as a " +
                        "credential problem; unset both to use the Databricks CLI broker, or set both to " +
                        "use the OAuth M2M flow.");
                }

                if (options.Host is null)
                {
                    // M2M cannot derive a token URL without a workspace host. Throw at start-up so
                    // a misconfigured environment fails loudly, not on the first Genie call.
                    throw new InvalidOperationException(
                        "OAuth M2M requires a Databricks host. Set GenieClientOptions.Host, DATABRICKS_HOST, " +
                        "or a profile in .databrickscfg.");
                }

                var tokenEndpoint = new Uri(options.Host, "/oidc/v1/token");
                return new M2mTokenProvider(tokenEndpoint, clientId, clientSecret);
            }

            return new DatabricksCliTokenProvider(options.Profile);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<GenieAuthenticationHandler>();

        var httpClientBuilder = services.AddHttpClient<IGenieClient, GenieClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<GenieClientOptions>>().Value;
                http.BaseAddress = options.Host;
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent.Value);
            })
            .AddHttpMessageHandler<GenieAuthenticationHandler>();

        var resilienceBuilder = httpClientBuilder.AddStandardResilienceHandler();

        // AddStandardResilienceHandler disables HttpClient.Timeout. Reapply the matching outer
        // deadline afterwards so ResponseContentRead buffering, which happens after a
        // DelegatingHandler has returned the headers, is bounded too.
        httpClientBuilder.ConfigureHttpClient((sp, http) =>
            http.Timeout = sp.GetRequiredService<IOptions<GenieClientOptions>>()
                .Value.RequestTimeout);

        resilienceBuilder.Configure((resilience, sp) =>
            {
                var requestTimeout = sp.GetRequiredService<IOptions<GenieClientOptions>>()
                    .Value.RequestTimeout;
                resilience.Retry.MaxRetryAttempts = 3;
                resilience.Retry.UseJitter = true;

                // The standard handler retries POST by default. That is wrong for this API:
                // start-conversation and create-message are not idempotent, so a retry after a
                // transient 5xx asks Genie the SAME question a second time — running the SQL
                // warehouse twice, billing twice, and leaving an orphaned conversation whose
                // id the client never returns. Reads stay retryable, which is where retrying
                // actually helps: the polling loop is almost all of the request volume.
#if NET9_0_OR_GREATER
                resilience.Retry.DisableForUnsafeHttpMethods();
#else
                resilience.Retry.ShouldHandle = GenieRetryPolicy.ShouldRetryResilience;
#endif

                resilience.AttemptTimeout.Timeout = requestTimeout < TimeSpan.FromSeconds(30)
                    ? requestTimeout
                    : TimeSpan.FromSeconds(30);
                resilience.TotalRequestTimeout.Timeout = requestTimeout;
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        return services;
    }

    /// <summary>Registers a token provider that calls the Databricks CLI for the named profile.</summary>
    public static IServiceCollection AddDatabricksCliAuthentication(
        this IServiceCollection services, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGenieTokenProvider>(_ => new DatabricksCliTokenProvider(profile));
        return services;
    }

    /// <summary>Registers a caller-supplied token source, for hosts with their own OAuth flow.</summary>
    public static IServiceCollection AddGenieTokenProvider(
        this IServiceCollection services, Func<CancellationToken, ValueTask<string>> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton<IGenieTokenProvider>(_ => new DelegateTokenProvider(factory));
        return services;
    }
}

internal static class UserAgent
{
    // Databricks correlates client traffic by user agent; an unidentified client is
    // indistinguishable from a script when someone is investigating workspace load.
    internal static string Value { get; } =
        $"LakeSpeak.NET/{typeof(UserAgent).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
}

/// <summary>
/// Retry predicate for the standard resilience handler on the net8 line of
/// Microsoft.Extensions.Http.Resilience (8.10.0), which does not expose
/// DisableForUnsafeHttpMethods. Mirrors that helper: transient check first, then
/// unsafe-method exclusion against the request method. The official helper falls back
/// to args.Context when the outcome's Result is null (a transient exception), and
/// so do we — otherwise a socket reset on a POST would still be retried. See ADR 0006.
/// </summary>
internal static class GenieRetryPolicy
{
    private static readonly HttpMethod[] UnsafeMethods =
    {
        HttpMethod.Post,
        HttpMethod.Put,
        HttpMethod.Patch,
        HttpMethod.Delete,
        HttpMethod.Connect,
    };

    internal static ValueTask<bool> ShouldRetryResilience(
        RetryPredicateArguments<HttpResponseMessage> args)
    {
        if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
        {
            return new ValueTask<bool>(false);
        }

        var method = args.Outcome.Result?.RequestMessage?.Method
            ?? args.Context.GetRequestMessage()?.Method;
        var isUnsafe = Array.IndexOf(UnsafeMethods, method) >= 0;
        return new ValueTask<bool>(!isUnsafe);
    }
}
