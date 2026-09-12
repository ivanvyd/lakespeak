using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LakeSpeak.Genie.Tests;

public class TransportFailureMappingTests
{
    private static readonly Uri Host = new("https://example.azuredatabricks.net");

    [Theory]
    [InlineData("timeout")]
    [InlineData("circuit")]
    public async Task Polly_transport_failures_are_mapped_to_redacted_typed_failures(string failure)
    {
        // Arrange — the opaque value is only secret-shaped through the named-key rule. Keeping it
        // in the third-party exception proves both Message and ToString are safe to log.
        const string secret = "opaque_transport_credential_123";
        Exception transport = failure switch
        {
            "timeout" => new TimeoutRejectedException($"access_token={secret}"),
            "circuit" => new BrokenCircuitException($"client_secret={secret}"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var client = new GenieClient(
            new HttpClient(new ThrowingHandler(transport)) { BaseAddress = Host },
            Options.Create(new GenieClientOptions { Host = Host }));

        // Act
        var exception = await Should.ThrowAsync<GenieException>(
            () => client.GetAgentAsync("agent", TestContext.Current.CancellationToken));

        // Assert
        exception.Kind.ShouldBe(GenieFailureKind.Network);
        exception.IsRetryable.ShouldBeTrue();
        exception.InnerException.ShouldBeSameAs(transport);
        exception.Message.ShouldNotContain(secret);
        exception.ToString().ShouldNotContain(secret);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("circuit")]
    public async Task AddLakeSpeak_pipeline_keeps_resilience_failures_inside_the_typed_boundary(string failure)
    {
        // Arrange — the registered client includes authentication and the standard resilience
        // pipeline. POST is intentionally used so the unsafe-method policy does not retry it.
        var services = new ServiceCollection();
        Exception transport = failure switch
        {
            "timeout" => new TimeoutRejectedException("access_token=opaque_pipeline_credential_123"),
            "circuit" => new BrokenCircuitException("client_secret=opaque_pipeline_credential_123"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        services.AddSingleton(new TransportFailure(transport));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, ThrowingPrimaryHandlerFilter>();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("synthetic-token"));
        services.AddLakeSpeak(options => options.Host = Host);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IGenieClient>();

        // Act
        var exception = await Should.ThrowAsync<GenieException>(() =>
            client.StartConversationAsync("agent", "question", TestContext.Current.CancellationToken));

        // Assert
        exception.Kind.ShouldBe(GenieFailureKind.Network);
        exception.InnerException.ShouldBeSameAs(transport);
        exception.ToString().ShouldNotContain("opaque_pipeline_credential_123");
    }

    [Fact]
    public void AddLakeSpeak_applies_the_request_timeout_to_the_body_inclusive_http_client()
    {
        var services = new ServiceCollection();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("synthetic-token"));
        services.AddLakeSpeak(options =>
        {
            options.Host = Host;
            options.RequestTimeout = TimeSpan.FromMilliseconds(11);
        });
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(nameof(IGenieClient)).Timeout.ShouldBe(TimeSpan.FromMilliseconds(11));
    }

    [Fact]
    public async Task AddLakeSpeak_bounds_a_response_body_that_never_completes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, SlowBodyPrimaryHandlerFilter>();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("synthetic-token"));
        services.AddLakeSpeak(options =>
        {
            options.Host = Host;
            options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IGenieClient>();

        var act = () => client.GetAgentAsync("agent", TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<GenieException>(act);
        exception.Kind.ShouldBe(GenieFailureKind.Network);
        exception.IsRetryable.ShouldBeTrue();
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class SlowBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SlowContent(),
            });
    }

    private sealed class SlowContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed record TransportFailure(Exception Exception);

    private sealed class ThrowingPrimaryHandlerFilter(TransportFailure failure)
        : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                builder.PrimaryHandler = new ThrowingHandler(failure.Exception);
            };
    }

    private sealed class SlowBodyPrimaryHandlerFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                builder.PrimaryHandler = new SlowBodyHandler();
            };
    }
}
