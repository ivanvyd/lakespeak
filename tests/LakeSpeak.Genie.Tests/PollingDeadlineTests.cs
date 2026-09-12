using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeSpeak.Genie.Tests;

public class PollingDeadlineTests
{
    private static readonly Uri Host = new("https://example.azuredatabricks.net");

    [Fact]
    public async Task Deadline_cancels_an_in_flight_poll_request()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var handler = new BlockingHandler();
        var client = CreateClient(handler, clock, TimeSpan.FromSeconds(10));

        // Act
        var task = client.WaitForResponseAsync(
            "agent", "conversation", "message", cancellationToken: TestContext.Current.CancellationToken);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));

        // Assert
        var exception = await Should.ThrowAsync<GenieException>(() => task);
        exception.Kind.ShouldBe(GenieFailureKind.PollingTimeout);
        handler.ObservedCancellation.ShouldBeTrue();
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public async Task Completion_is_accepted_only_before_the_deadline(int responseAtSeconds, bool succeeds)
    {
        // Arrange — the handler deliberately ignores cancellation to model a transport that
        // completes during the cancellation race. The client must check the clock after it lands.
        var clock = new FakeTimeProvider();
        var handler = new ClockAdvancingHandler(clock, TimeSpan.FromSeconds(responseAtSeconds));
        var client = CreateClient(handler, clock, TimeSpan.FromSeconds(10));

        // Act
        var task = client.WaitForResponseAsync(
            "agent", "conversation", "message", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        if (succeeds)
        {
            (await task).State.ShouldBe(GenieMessageState.Completed);
        }
        else
        {
            var exception = await Should.ThrowAsync<GenieException>(() => task);
            exception.Kind.ShouldBe(GenieFailureKind.PollingTimeout);
        }
    }

    [Fact]
    public async Task Poll_delay_is_clamped_to_the_remaining_deadline()
    {
        // Arrange — after the first pending response only two seconds remain, while the configured
        // poll interval is five. Advancing exactly the deadline must settle the operation.
        var clock = new FakeTimeProvider();
        var handler = new PendingHandler();
        var client = CreateClient(
            handler,
            clock,
            TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromSeconds(5));

        // Act
        var task = client.WaitForResponseAsync(
            "agent", "conversation", "message", cancellationToken: TestContext.Current.CancellationToken);
        await handler.FirstResponse.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(2));

        // Assert
        var exception = await Should.ThrowAsync<GenieException>(() => task);
        exception.Kind.ShouldBe(GenieFailureKind.PollingTimeout);
        handler.RequestCount.ShouldBe(1);
    }

    private static GenieClient CreateClient(
        HttpMessageHandler handler,
        TimeProvider clock,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var http = new HttpClient(handler) { BaseAddress = Host };
        return new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = Host,
            PollingTimeout = timeout,
            InitialPollInterval = interval,
            MaxPollInterval = interval,
        }), clock);
    }

    private static HttpResponseMessage Message(string status) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"space_id":"agent","conversation_id":"conversation","message_id":"message","status":"{{status}}"}""",
            System.Text.Encoding.UTF8,
            "application/json"),
    };

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ObservedCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The infinite delay unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private sealed class ClockAdvancingHandler(FakeTimeProvider clock, TimeSpan advance) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            clock.Advance(advance);
            return Task.FromResult(Message("COMPLETED"));
        }
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource FirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            FirstResponse.TrySetResult();
            return Task.FromResult(Message("EXECUTING_QUERY"));
        }
    }
}
