using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using LakeSpeak.Genie;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeSpeak.ContractTests;

/// <summary>
/// Two properties that are invisible when they break: a partial result that claims to be
/// complete, and a non-idempotent request that quietly runs twice.
/// </summary>
public sealed class ResultCompletenessTests : IDisposable
{
    private const string Agent = "a";
    private const string Conversation = "c";
    private const string Message = "m";
    private const string Attachment = "att";

    private readonly WireMockServer _server = WireMockServer.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GenieClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        return new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
        }));
    }

    private void StubQueryResult(
        string resultJson,
        string manifestExtra = "",
        string? statementId = null)
    {
        var statementIdProperty = statementId is null
            ? string.Empty
            : $"\"statement_id\": {JsonSerializer.Serialize(statementId)},";

        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}/attachments/{Attachment}/query-result")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""
                {
                  "statement_response": {
                    {{statementIdProperty}}
                    "manifest": {
                      "truncated": false{{manifestExtra}},
                      "schema": { "columns": [ { "name": "region", "type_text": "STRING" } ] }
                    },
                    "result": {{resultJson}}
                  }
                }
                """));
    }

    private void StubChunk(int index, string body)
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/sql/statements/s1/result/chunks/{index}")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));
    }

    /// <summary>
    /// A chunk that advertises a successor but carries no link to it leaves the client with no
    /// way to complete the result. <c>manifest.truncated</c> reports statement-level truncation
    /// by Databricks and is false for a merely-chunked result, so relying on it alone hands back
    /// the first chunk labelled complete — a partial export nobody knows is partial.
    /// </summary>
    [Fact]
    public async Task A_chunked_result_with_no_link_to_follow_is_reported_as_truncated()
    {
        // Arrange
        StubQueryResult(
            """{ "row_count": 2, "chunk_index": 0, "next_chunk_index": 1, "data_array": [["Germany"],["France"]] }""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(2);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// The Genie query-result endpoint omits <c>next_chunk_internal_link</c> even when the SQL
    /// Statement response has a readable next chunk. The statement id and next index are enough
    /// to use the documented chunk endpoint without guessing another host.
    /// </summary>
    [Fact]
    public async Task A_Genie_result_without_a_chunk_link_uses_its_statement_id()
    {
        // Arrange — observed live: Genie keeps the statement id and next index but drops the link.
        StubQueryResult(
            """{ "row_count": 1, "chunk_index": 0, "next_chunk_index": 1, "data_array": [["Germany"]] }""",
            manifestExtra: """, "total_row_count": 2""",
            statementId: "s1");
        StubChunk(1, """{ "row_count": 1, "chunk_index": 1, "data_array": [["France"]] }""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Select(r => r[0]).ShouldBe(["Germany", "France"]);
        result.IsTruncated.ShouldBeFalse();
        result.TotalRowCount.ShouldBe(2);
    }

    /// <summary>
    /// <see cref="Uri"/> normalizes dot-only path segments even after
    /// <see cref="Uri.EscapeDataString(string)"/>. A malformed statement id must therefore stop
    /// as an incomplete result instead of addressing a different SQL endpoint.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_dot_segment_statement_id_is_not_used_as_a_chunk_path(string statementId)
    {
        // Arrange
        StubQueryResult(
            """{ "row_count": 1, "chunk_index": 0, "next_chunk_index": 1, "data_array": [["Germany"]] }""",
            manifestExtra: """, "total_row_count": 2""",
            statementId: statementId);
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBe(0);
    }

    /// <summary>
    /// The whole point of the change: a result split across chunks comes back whole, in order,
    /// and is not flagged as truncated because nothing is missing.
    /// </summary>
    [Fact]
    public async Task A_chunked_result_is_assembled_across_every_chunk()
    {
        // Arrange — three chunks, each pointing at the next by its documented internal link.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 3""");

        StubChunk(1,
            """
            {
              "row_count": 1, "chunk_index": 1, "next_chunk_index": 2,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/2",
              "data_array": [["France"]]
            }
            """);

        StubChunk(2, """{ "row_count": 1, "chunk_index": 2, "data_array": [["Spain"]] }""");

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Select(r => r[0]).ShouldBe(["Germany", "France", "Spain"]);
        result.IsTruncated.ShouldBeFalse();
        result.TotalRowCount.ShouldBe(3);
    }

    /// <summary>
    /// A chunk the caller may not read degrades to the rows already in hand. Genie executes the
    /// statement, so whether the caller can read its remaining chunks is a workspace permission
    /// question — and losing a good answer over a missing tail would be the worse trade.
    /// </summary>
    [Fact]
    public async Task An_unreachable_chunk_returns_the_rows_so_far_and_reports_truncated()
    {
        // Arrange
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 2""");

        _server.Given(Request.Create()
                .WithPath("/api/2.0/sql/statements/s1/result/chunks/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403));

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// The chunk link is server-supplied and every workspace request carries the bearer token, so
    /// a link naming another host would disclose that token to it. Both forms below resolve to
    /// <c>evil.example.com</c> — the protocol-relative one despite looking like a path, which is
    /// why the check is on the resolved host rather than the shape of the string.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example.com/api/2.0/sql/statements/s1/result/chunks/1")]
    [InlineData("//evil.example.com/api/2.0/sql/statements/s1/result/chunks/1")]
    public async Task A_next_chunk_link_pointing_off_the_workspace_is_refused(string link)
    {
        // Arrange
        StubQueryResult(
            $$"""
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "{{link}}",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 2""");

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert — the rows in hand, flagged, and nothing sent off-host.
        result!.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBe(0);
    }

    /// <summary>
    /// A later chunk arriving under EXTERNAL_LINKS disposition must not destroy the answer.
    /// </summary>
    /// <remarks>
    /// Refusing external links is right when it is the *first* chunk: there is nothing to hand
    /// back, and an empty result reading as a real one is the worst outcome available. Once rows
    /// and an answer are already in hand, throwing discards both — and the answer text is the
    /// primary payload, which is exactly why <c>CompleteAsync</c> tolerates a missing result.
    /// </remarks>
    [Fact]
    public async Task A_later_chunk_under_external_links_keeps_the_rows_and_reports_truncated()
    {
        // Arrange — chunk zero is inline and fine; chunk one fetches successfully but carries
        // links instead of rows.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 2""");

        StubChunk(1, """{ "chunk_index": 1, "external_links": [ { "chunk_index": 1, "row_count": 1 } ] }""");

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert — the rows already assembled survive, flagged short.
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// A chunk that points at itself would spin forever, and a self-pointing chunk carrying no
    /// rows would not even grow toward <c>MaxResultRows</c> — so the row cap is not the backstop
    /// here. This test would hang rather than fail if the guard were removed.
    /// </summary>
    [Fact]
    public async Task A_chunk_link_that_repeats_stops_rather_than_looping()
    {
        // Arrange — chunk one points back at itself and carries no rows.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 9000""");

        StubChunk(1,
            """
            {
              "row_count": 0, "chunk_index": 1, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": []
            }
            """);

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// The row cap and the repeated-link guard both fail against a response that hands back a
    /// <em>fresh</em> link every time while carrying no rows: nothing approaches
    /// <c>MaxResultRows</c>, and no link is ever seen twice.
    /// </summary>
    /// <remarks>
    /// Found by an adversarial review, which drove 680,307 authenticated requests before a
    /// five-second token cut it off. Without an iteration bound this test does not fail — it
    /// hangs, and in production it is an unbounded request flood at a real workspace.
    /// </remarks>
    [Fact]
    public async Task An_endless_walk_of_fresh_links_is_bounded_rather_than_followed_forever()
    {
        // Arrange — chunk N always points at a distinct chunk N+1 and never returns a row.
        StubQueryResult(
            """
            {
              "row_count": 0, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": []
            }
            """,
            manifestExtra: """, "total_row_count": 9000""");

        _server.Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher(@"^/api/2\.0/sql/statements/s1/result/chunks/\d+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(request =>
            {
                var index = int.Parse(request.Path.Split('/')[^1], CultureInfo.InvariantCulture);
                return new WireMock.ResponseMessage
                {
                    StatusCode = 200,
                    BodyData = new WireMock.Util.BodyData
                    {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString =
                            $$"""
                            {
                              "row_count": 0, "chunk_index": {{index}}, "next_chunk_index": {{index + 1}},
                              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/{{index + 1}}",
                              "data_array": []
                            }
                            """,
                    },
                };
            }));

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert — it terminates, and says the result is short.
        result.ShouldNotBeNull();
        result.IsTruncated.ShouldBeTrue();

        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBeLessThanOrEqualTo(GenieClient.MaxChunkFetches);
    }

    /// <summary>
    /// Following a chunked result to its end is unbounded work against a billed warehouse's
    /// output. The cap stops it — and says so, rather than reporting a capped result as whole.
    /// </summary>
    [Fact]
    public async Task Reaching_MaxResultRows_stops_the_walk_and_reports_truncated()
    {
        // Arrange — chunk zero already meets the cap, so chunk one is never requested.
        StubQueryResult(
            """
            {
              "row_count": 2, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"],["France"]]
            }
            """,
            manifestExtra: """, "total_row_count": 4""");

        StubChunk(1, """{ "row_count": 2, "chunk_index": 1, "data_array": [["Spain"],["Italy"]] }""");

        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
            MaxResultRows = 2,
        }));

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(2);
        result.IsTruncated.ShouldBeTrue();
        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBe(0);
    }

    [Fact]
    public async Task An_oversized_first_chunk_is_returned_whole_without_fetching_another_chunk()
    {
        // Arrange — MaxResultRows is a fetch threshold, not a hard returned-row limit. Trimming
        // rows which already arrived would silently alter the documented public contract.
        StubQueryResult(
            """
            {
              "row_count": 3, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"],["France"],["Spain"]]
            }
            """,
            manifestExtra: """, "total_row_count": 5""");

        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
            MaxResultRows = 2,
        }));

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(3);
        result.IsTruncated.ShouldBeTrue();
        _server.LogEntries.Count(entry =>
            entry.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true).ShouldBe(0);
    }

    [Fact]
    public async Task An_oversized_later_chunk_is_returned_whole_then_stops_further_fetches()
    {
        // Arrange — the client is below the threshold when chunk one is requested. All cells in
        // that response remain visible even though appending it crosses the threshold.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 6""");
        StubChunk(1,
            """
            {
              "row_count": 3, "chunk_index": 1, "next_chunk_index": 2,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/2",
              "data_array": [["France"],["Spain"],["Italy"]]
            }
            """);
        StubChunk(2, """{ "row_count": 2, "chunk_index": 2, "data_array": [["Japan"],["Brazil"]] }""");

        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
            MaxResultRows = 2,
        }));

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(4);
        result.IsTruncated.ShouldBeTrue();
        _server.LogEntries.Count(entry =>
            entry.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true).ShouldBe(1);
    }

    [Fact]
    public async Task A_short_read_against_the_manifest_row_count_is_reported_as_truncated()
    {
        // Arrange — the same failure reached a different way: the manifest advertises more rows
        // than arrived.
        StubQueryResult(
            """{ "row_count": 1, "data_array": [["Germany"]] }""",
            manifestExtra: """, "total_row_count": 5000""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.IsTruncated.ShouldBeTrue();
        result.TotalRowCount.ShouldBe(5000);
    }

    [Fact]
    public async Task A_complete_single_chunk_result_is_not_reported_as_truncated()
    {
        // Arrange — the conservative truncation check must not false-positive on a whole result.
        StubQueryResult(
            """{ "row_count": 2, "chunk_index": 0, "data_array": [["Germany"],["France"]] }""",
            manifestExtra: """, "total_row_count": 2""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.IsTruncated.ShouldBeFalse();
    }

    /// <summary>
    /// Under EXTERNAL_LINKS disposition the rows sit behind presigned URLs and
    /// <c>data_array</c> is absent. Returning an empty row set as a successful, complete result
    /// would be the worst outcome available — a silently empty export that looks like an answer.
    /// </summary>
    [Fact]
    public async Task An_external_links_result_is_refused_rather_than_returned_empty()
    {
        // Arrange — no data_array, no total_row_count, no next_chunk_index.
        StubQueryResult(
            """{ "external_links": [ { "chunk_index": 0, "row_count": 100000 } ] }""");
        var client = CreateClient();

        // Act
        var act = () => client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBe(GenieFailureKind.UnsupportedResult);
        ex.Message.ShouldContain("100000");
    }

    /// <summary>
    /// Re-executing an expired result must return the rows, not the acknowledgement.
    /// </summary>
    /// <remarks>
    /// Observed against a live workspace: <c>execute-query</c> answers HTTP 200 with state
    /// <c>PENDING</c> and no manifest — it only starts the re-execution — and the rows arrive on
    /// the ordinary query-result endpoint a moment later. Returning that first response gave the
    /// caller nothing, so <c>export last</c> told people to ask the question again while the
    /// warehouse work they had just paid for completed and was thrown away.
    /// </remarks>
    [Fact]
    public async Task A_re_executed_query_returns_the_rows_rather_than_the_pending_acknowledgement()
    {
        // Arrange — execute-query acknowledges without a manifest, exactly as Databricks does.
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}/attachments/{Attachment}/execute-query")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{ "statement_response": { "status": { "state": "PENDING" } } }"""));

        StubQueryResult(
            """{ "row_count": 1, "chunk_index": 0, "data_array": [["Germany"]] }""",
            manifestExtra: """, "total_row_count": 1""");

        var client = CreateClient();

        // Act
        var result = await client.ReExecuteQueryAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeFalse();
    }

    /// <summary>
    /// start-conversation is not idempotent: a retry asks Genie the same question again, running
    /// the SQL warehouse a second time and billing for it, and leaves an orphaned conversation
    /// whose id the caller never receives.
    /// </summary>
    [Fact]
    public async Task A_failed_start_conversation_is_never_retried()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/start-conversation").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var services = new ServiceCollection();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("token"));
        services.AddLakeSpeak(o => o.Host = new Uri("https://example.azuredatabricks.net"));
        using var provider = services.BuildServiceProvider();

        // The configured client is pointed at the stub without disturbing the resilience pipeline.
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IGenieClient));
        http.BaseAddress = new Uri(_server.Url!);
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
        }));

        // Act
        await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        // Assert
        var attempts = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.EndsWith("start-conversation", StringComparison.Ordinal) == true);
        attempts.ShouldBe(1);
    }

    /// <summary>
    /// Same idempotency contract as <see cref="A_failed_start_conversation_is_never_retried"/>,
    /// but on the exception path: a transient <see cref="HttpRequestException"/> (connection
    /// refused on a closed port) must not be retried on POST either. The standard resilience
    /// handler's <c>ShouldHandle</c> predicate only sees the request method via
    /// <c>args.Context</c> on this branch — without the context fallback, a socket reset on
    /// <c>start-conversation</c> re-issues the POST and runs the SQL warehouse twice.
    /// </summary>
    [Fact]
    public async Task A_connection_refused_start_conversation_is_never_retried()
    {
        // The host must satisfy the GenieClientOptions URL validator (https only — a bearer
        // token over http is silently disclosed), but the actual request is routed to the
        // refused-port URL via the named HttpClient's BaseAddress. The validator runs at
        // options-validation time, before the request is sent, so the validator is satisfied
        // by the https URL while the OS-level connect attempt goes to the refused port.
        var refusedPort = FindClosedPort();
        var counter = new StartConversationCounter();

        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, StartConversationCountingFilter>();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("token"));
        services.AddLakeSpeak(o => o.Host = new Uri("https://example.azuredatabricks.net"));
        using var provider = services.BuildServiceProvider();

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IGenieClient));
        // Override the BaseAddress that AddLakeSpeak set from the options. The validator is
        // already satisfied; the resolver is what GenieClient actually uses for outgoing calls.
        http.BaseAddress = new Uri($"http://127.0.0.1:{refusedPort}");
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
        }));

        // Act
        await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        // Assert
        counter.StartConversationCount.ShouldBe(1);
    }

    private static int FindClosedPort()
    {
        // Bind to port 0 to let the OS pick a free port, then close the listener so the
        // port is "free but refused on connect" — the canonical "connection refused" target.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class StartConversationCounter
    {
        public int StartConversationCount;
    }

    private sealed class StartConversationCountingFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                var counter = builder.Services.GetRequiredService<StartConversationCounter>();
                builder.AdditionalHandlers.Add(new StartConversationCounterHandler(counter));
            };
    }

    private sealed class StartConversationCounterHandler : DelegatingHandler
    {
        private readonly StartConversationCounter _counter;

        public StartConversationCounterHandler(StartConversationCounter counter)
        {
            _counter = counter;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("start-conversation", StringComparison.Ordinal) == true)
            {
                _counter.StartConversationCount++;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public void Dispose() => _server.Dispose();
}
