using LakeSpeak.Genie;
using NSubstitute;

namespace LakeSpeak.QuestionPacks.Tests;

public sealed class PackRunnerTests
{
    [Fact]
    public async Task Questions_run_sequentially_in_fresh_conversations()
    {
        var client = Substitute.For<IGenieClient>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<GenieResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                firstStarted.SetResult();
                return releaseFirst.Task;
            });
        client.AskAsync("agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("conversation-2", "message-2", "second answer"));

        var run = new PackRunner(client).RunAsync(Pack(
            new PackQuestion("first", null, "first", null),
            new PackQuestion("second", null, "second", null)), "agent-1", "Agent",
            cancellationToken: TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
        releaseFirst.SetResult(Response("conversation-1", "message-1", "first answer"));
        var result = await run;

        Received.InOrder(() =>
        {
            client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
            client.AskAsync("agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
        });
        result.Outcomes.Select(o => o.Response!.ConversationId)
            .ShouldBe(["conversation-1", "conversation-2"]);
    }

    [Fact]
    public async Task Continue_on_failure_collects_mixed_outcomes_in_order()
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GenieResponse>(
                new GenieException(GenieFailureKind.MessageFailed, "query failed")));
        client.AskAsync("agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("conversation-2", "message-2", "answer"));

        var result = await new PackRunner(client).RunAsync(Pack(
            new PackQuestion("first", null, "first", null),
            new PackQuestion("second", null, "second", null)), "agent-1", null,
            cancellationToken: TestContext.Current.CancellationToken);

        result.AnyFailed.ShouldBeTrue();
        result.FailureCount.ShouldBe(1);
        result.Outcomes.Select(o => o.Question.Id).ShouldBe(["first", "second"]);
        result.Outcomes[0].Failure.ShouldBe("query failed");
        result.Outcomes[1].Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_on_failure_propagates_and_does_not_ask_later_questions()
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GenieResponse>(
                new GenieException(GenieFailureKind.MessageFailed, "query failed")));
        var pack = Pack(
            new PackQuestion("first", null, "first", null),
            new PackQuestion("second", null, "second", null)) with
        {
            Behavior = Behavior() with { ContinueOnQuestionFailure = false },
        };

        await Should.ThrowAsync<GenieException>(() =>
            new PackRunner(client).RunAsync(
                pack, "agent-1", null,
                cancellationToken: TestContext.Current.CancellationToken));

        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Per_question_timeout_overrides_the_pack_default()
    {
        var client = Substitute.For<IGenieClient>();
        var timeouts = new List<TimeSpan?>();
        client.AskAsync("agent-1", Arg.Any<string>(), Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                timeouts.Add(call.ArgAt<GenieAskOptions>(2).Timeout);
                return Response($"conversation-{timeouts.Count}", $"message-{timeouts.Count}", "answer");
            });
        var pack = Pack(
            new PackQuestion("first", null, "first", TimeSpan.FromSeconds(9)),
            new PackQuestion("second", null, "second", null)) with
        {
            Behavior = Behavior() with { Timeout = TimeSpan.FromMinutes(3) },
        };

        await new PackRunner(client).RunAsync(
            pack, "agent-1", null,
            cancellationToken: TestContext.Current.CancellationToken);

        timeouts.ShouldBe([TimeSpan.FromSeconds(9), TimeSpan.FromMinutes(3)]);
    }

    [Fact]
    public async Task Caller_cancellation_stops_before_the_next_question()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Response("conversation-1", "message-1", "answer");
            });

        await Should.ThrowAsync<OperationCanceledException>(() => new PackRunner(client).RunAsync(
            Pack(
                new PackQuestion("first", null, "first", null),
                new PackQuestion("second", null, "second", null)),
            "agent-1", null, cancellationToken: cancellation.Token));

        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Streaming_run_retains_compact_summaries_and_emits_each_outcome_in_order()
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", Arg.Any<string>(), Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Response(
                $"conversation-{call.ArgAt<string>(1)}", "message", $"answer-{call.ArgAt<string>(1)}"));
        var emitted = new List<QuestionOutcome>();

        var result = await new PackRunner(client).RunStreamingAsync(
            Pack(
                new PackQuestion("first", null, "first", null),
                new PackQuestion("second", null, "second", null)),
            "agent-1",
            "Agent",
            (outcome, _) =>
            {
                emitted.Add(outcome);
                return ValueTask.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        emitted.Select(o => o.Question.Id).ShouldBe(["first", "second"]);
        result.Outcomes.Select(o => o.Question.Id).ShouldBe(["first", "second"]);
        result.Outcomes.ShouldAllBe(o => o.GetType() == typeof(QuestionOutcomeSummary));
    }

    [Fact]
    public async Task Streaming_sink_failures_are_propagated_once_and_are_not_question_failures()
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("conversation", "message", "answer"));
        var calls = 0;

        var ex = await Should.ThrowAsync<GenieException>(() => new PackRunner(client).RunStreamingAsync(
            Pack(new PackQuestion("first", null, "first", null)),
            "agent-1",
            null,
            (_, _) =>
            {
                calls++;
                throw new GenieException(GenieFailureKind.Unexpected, "sink failed");
            },
            cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("sink failed");
        calls.ShouldBe(1);
    }

    private static QuestionPack Pack(params PackQuestion[] questions) => new(
        "pack-name", "Description", "agent", null, questions,
        new PackOutput("markdown", null), Behavior());

    private static PackBehavior Behavior() => new(
        ContinueOnQuestionFailure: true,
        IncludeGeneratedSql: true,
        IncludeTimings: true,
        IncludeIdentifiers: true,
        Timeout: TimeSpan.FromMinutes(10));

    private static GenieResponse Response(string conversation, string message, string text) => new(
        "agent-1", conversation, message, GenieMessageState.Completed, text, null, null, [],
        new GenieResponseMetadata(TimeSpan.Zero, 1));
}
