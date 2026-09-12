using System.CommandLine;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class FeedbackCommand
{
    private static readonly Argument<string> Target =
        new("target")
        {
            Description = "Which answer to rate. Currently only 'last'.",
            DefaultValueFactory = _ => "last",
        };

    private static readonly Option<string> Rating =
        new("--rating", "-r") { Description = "positive, negative, or none.", Required = true };

    private static readonly Option<string?> Comment =
        new("--comment", "-c") { Description = "Optional free-text comment sent to Databricks." };

    internal static Command Create()
    {
        var command = new Command("feedback", "Rate the last answer, without opening a chat session.")
        {
            Target,
            Rating,
            Comment,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(
                parseResult,
                (host, ct) => RunAsync(host, parseResult, ct),
                cancellationToken,
                ProfileRequest()));

        return command;
    }

    internal static CliProfileRequest ProfileRequest() =>
        CliProfileRequest.ForLastAnswer();

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        CliArguments.RequireLastTarget(parseResult.GetValue(Target));

        var rating = parseResult.GetValue(Rating) switch
        {
            "positive" => GenieFeedbackRating.Positive,
            "negative" => GenieFeedbackRating.Negative,
            "none" => GenieFeedbackRating.None,
            var other => throw new CliUsageException(
                $"Unknown rating '{other}'. Use positive, negative, or none."),
        };

        // The pointer records which profile the conversation lives in. Without consulting it,
        // this command would resolve the profile from the flag or config default and could
        // address a different workspace than the answer came from.
        var recent = host.RecentConversation
            ?? throw new CliUsageException(
                "No previous answer to rate. Run `lakespeak ask` first.");

        if (recent.AgentId is null || recent.ConversationId is null || recent.MessageId is null)
        {
            throw new CliUsageException(
                "The stored pointer to the last answer is incomplete. Run `lakespeak ask` again.");
        }

        await host.Client.SendFeedbackAsync(
            recent.AgentId, recent.ConversationId, recent.MessageId,
            rating, parseResult.GetValue(Comment), cancellationToken).ConfigureAwait(false);

        host.Output.Error.MarkupLine(
            $"[green]Feedback sent[/] for the last answer from " +
            $"[bold]{Markup.Escape(TerminalSafety.Sanitize(recent.AgentTitle ?? recent.AgentId))}[/].");

        return ExitCode.Success;
    }
}
