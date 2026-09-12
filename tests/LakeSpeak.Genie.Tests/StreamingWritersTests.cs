using System.Text;
using LakeSpeak.Rendering;

namespace LakeSpeak.Genie.Tests;

public class StreamingWritersTests
{
    private static GenieResponse Response() => new(
        "agent",
        "conversation",
        "message",
        GenieMessageState.Completed,
        "answer",
        new GenieQuery(
            "SELECT value FROM source WHERE region = :region",
            Parameters: [new GenieQueryParameter("region", "STRING", "West")]),
        new GenieQueryResult(
            [new GenieColumn("value", "STRING", "STRING")],
            [["one"], ["two"]],
            IsTruncated: true,
            TotalRowCount: 3),
        [],
        new GenieResponseMetadata(TimeSpan.FromSeconds(1), 2));

    [Fact]
    public async Task Async_writers_match_the_corrected_convenience_outputs()
    {
        // Arrange
        var response = Response();
        using var jsonLines = new StringWriter();
        using var csv = new StringWriter();
        using var markdown = new StringWriter();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await MachineOutput.WriteJsonLinesAsync(jsonLines, response, cancellationToken: cancellationToken);
        await CsvWriter.WriteAsync(csv, response.Result!, cancellationToken);
        await MarkdownWriter.WriteAsync(markdown, response, "Finance", cancellationToken);

        // Assert
        jsonLines.ToString().ShouldBe(MachineOutput.ToJsonLines(response));
        csv.ToString().ShouldBe(CsvWriter.Write(response.Result!));
        markdown.ToString().ShouldBe(MarkdownWriter.Write(response, "Finance"));
    }

    [Fact]
    public async Task Json_lines_reaches_the_sink_one_row_at_a_time_and_honors_cancellation()
    {
        // Arrange
        var response = Response();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var writer = new CancelAfterFirstLineWriter(cancellation);

        // Act
        var act = () => MachineOutput.WriteJsonLinesAsync(
            writer,
            response,
            cancellationToken: cancellation.Token);

        // Assert — the first row reached the destination and cancellation prevented encoding and
        // writing the second. The writer never held a complete document.
        await Should.ThrowAsync<OperationCanceledException>(act);
        writer.Lines.ShouldBe(["{\"value\":\"one\"}"]);
    }

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation) : TextWriter
    {
        private readonly StringBuilder _current = new();

        internal List<string> Lines { get; } = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current.Append(buffer.Span);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current.Append(buffer.Span);
            Lines.Add(_current.ToString());
            _current.Clear();
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }
}
