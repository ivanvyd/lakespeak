using Microsoft.Extensions.Options;

namespace LakeSpeak.Genie.Tests;

public class GenieClientOptionsTests
{
    private static GenieClientOptions ValidOptions() => new()
    {
        Host = new Uri("https://example.azuredatabricks.net"),
    };

    [Theory]
    [InlineData("polling")]
    [InlineData("initial")]
    [InlineData("maximum")]
    [InlineData("request")]
    public void Non_positive_time_options_are_rejected(string property)
    {
        // Arrange
        var options = ValidOptions();
        switch (property)
        {
            case "polling": options.PollingTimeout = TimeSpan.Zero; break;
            case "initial": options.InitialPollInterval = TimeSpan.Zero; break;
            case "maximum": options.MaxPollInterval = TimeSpan.Zero; break;
            case "request": options.RequestTimeout = TimeSpan.Zero; break;
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }

        // Act
        var act = options.Validate;

        // Assert
        Should.Throw<InvalidOperationException>(act).Message.ShouldContain(
            property == "request" ? nameof(GenieClientOptions.RequestTimeout) : "positive");
    }

    [Fact]
    public void Infinite_request_timeout_is_rejected()
    {
        var options = ValidOptions();
        options.RequestTimeout = Timeout.InfiniteTimeSpan;

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain(nameof(GenieClientOptions.RequestTimeout));
    }

    [Fact]
    public void Request_timeout_at_Polly_minimum_is_rejected()
    {
        var options = ValidOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(10);

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain(nameof(GenieClientOptions.RequestTimeout));
    }

    [Fact]
    public void Request_timeout_at_Polly_maximum_is_rejected()
    {
        var options = ValidOptions();
        options.RequestTimeout = TimeSpan.FromHours(24);

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain(nameof(GenieClientOptions.RequestTimeout));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(86_399_999)]
    public void Request_timeout_inside_Polly_bounds_is_accepted(int milliseconds)
    {
        var options = ValidOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(milliseconds);

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Non_positive_page_size_is_rejected()
    {
        var options = ValidOptions();
        options.PageSize = 0;

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain(nameof(GenieClientOptions.PageSize));
    }

    [Fact]
    public void Per_ask_timeout_must_be_positive_before_a_request_is_sent()
    {
        var options = ValidOptions();
        var handler = new CountingHandler();
        var client = new GenieClient(new HttpClient(handler), Options.Create(options));

        var act = () => client.WaitForResponseAsync(
            "agent", "conversation", "message", new GenieAskOptions { Timeout = TimeSpan.Zero });

        Should.Throw<ArgumentOutOfRangeException>(act);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Ask_validates_its_timeout_before_starting_a_conversation()
    {
        var options = ValidOptions();
        var handler = new CountingHandler();
        var client = new GenieClient(new HttpClient(handler), Options.Create(options));

        var act = () => client.AskAsync(
            "agent",
            "question",
            new GenieAskOptions { Timeout = TimeSpan.FromHours(1000) },
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(act);
        handler.RequestCount.ShouldBe(0);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
