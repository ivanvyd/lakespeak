using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LakeSpeak.Genie;

/// <inheritdoc cref="IGenieClient"/>
public sealed partial class GenieClient : IGenieClient
{
    private const string Root = "api/2.0/genie/spaces";

    /// <summary>
    /// Hard bound on chunk requests for one result, independent of how many rows each carries.
    /// </summary>
    /// <remarks>
    /// A chunk observed live held roughly 41,000 rows, so even a caller raising
    /// <see cref="GenieClientOptions.MaxResultRows"/> into the millions stays far inside this.
    /// It exists for the case the row cap cannot see: chunks that carry no rows at all.
    /// </remarks>
    internal const int MaxChunkFetches = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly GenieClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GenieClient> _logger;

    public GenieClient(
        HttpClient http,
        IOptions<GenieClientOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<GenieClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _options.Validate();
        // Injected so polling tests advance a fake clock instead of sleeping. A polling loop
        // tested with real delays is either slow or flaky, usually both.
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GenieClient>.Instance;
    }

    public async Task<GenieAgentPage> ListAgentsAsync(
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Root}?page_size={_options.PageSize}";
        if (!string.IsNullOrEmpty(pageToken))
        {
            url += $"&page_token={Uri.EscapeDataString(pageToken)}";
        }

        var wire = await GetAsync<SpaceListWire>(url, cancellationToken).ConfigureAwait(false);

        var agents = (wire.Spaces ?? [])
            .Where(s => !string.IsNullOrEmpty(s.SpaceId))
            .Select(s => new GenieAgent(s.SpaceId!, s.Title ?? s.SpaceId!, s.Description, s.WarehouseId))
            .ToList();

        return new GenieAgentPage(agents, wire.NextPageToken);
    }

    public async IAsyncEnumerable<GenieAgent> ListAllAgentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? token = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            var page = await ListAgentsAsync(token, cancellationToken).ConfigureAwait(false);
            foreach (var agent in page.Agents)
            {
                yield return agent;
            }

            token = page.NextPageToken;

            // A server that returns the same token forever would otherwise spin here until the
            // process is killed.
            if (token is not null && !seenTokens.Add(token))
            {
                LogRepeatedPageToken(_logger);
                yield break;
            }
        }
        while (!string.IsNullOrEmpty(token));
    }

    public async Task<GenieAgent> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var wire = await GetAsync<SpaceWire>($"{Root}/{Esc(agentId)}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(wire.SpaceId))
        {
            throw new GenieException(GenieFailureKind.MalformedResponse, "Agent response contained no space_id.");
        }

        return new GenieAgent(wire.SpaceId, wire.Title ?? wire.SpaceId, wire.Description, wire.WarehouseId);
    }

    public async Task<(GenieConversationRef Conversation, string MessageId)> StartConversationAsync(
        string agentId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await PostAsync<StartConversationRequestWire, StartConversationWire>(
            $"{Root}/{Esc(agentId)}/start-conversation",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(wire.ConversationId) || string.IsNullOrEmpty(wire.MessageId))
        {
            throw new GenieException(
                GenieFailureKind.MalformedResponse,
                "Start-conversation response was missing conversation_id or message_id.");
        }

        return (new GenieConversationRef(agentId, wire.ConversationId, wire.Conversation?.Title), wire.MessageId);
    }

    public async Task<string> SendMessageAsync(
        string agentId,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await PostAsync<StartConversationRequestWire, MessageWire>(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        return wire.MessageId ?? wire.Id
            ?? throw new GenieException(GenieFailureKind.MalformedResponse, "Message response contained no message_id.");
    }

    public async Task<GenieResponse> GetMessageAsync(
        string agentId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var wire = await GetAsync<MessageWire>(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}",
            cancellationToken).ConfigureAwait(false);

        return Normalize(wire, agentId, conversationId, messageId, TimeSpan.Zero, pollCount: 0);
    }

    public async Task<GenieResponse> WaitForResponseAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GenieAskOptions();
        var timeout = options.Timeout ?? _options.PollingTimeout;
        GenieClientOptions.ValidateWaitTimeout(timeout, nameof(options.Timeout));
        var started = _time.GetTimestamp();
        var interval = _options.InitialPollInterval;
        var polls = 0;
        GenieMessageState? lastState = null;
        GenieResponse? last = null;

        using var deadlineCancellation = new CancellationTokenSource(timeout, _time);
        using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = _time.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    throw PollingTimedOut(timeout, lastState, last);
                }

                var wire = await GetAsync<MessageWire>(
                    $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}",
                    pollingCancellation.Token).ConfigureAwait(false);

                polls++;
                last = Normalize(wire, agentId, conversationId, messageId, _time.GetElapsedTime(started), polls);

                // A handler can complete after cancellation was requested, so the clock remains
                // authoritative even when the transport returns a terminal response.
                elapsed = _time.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    throw PollingTimedOut(timeout, last.State, last);
                }

                if (last.State != lastState)
                {
                    lastState = last.State;
                    options.OnStateChanged?.Invoke(last.State);
                    LogStateChanged(_logger, messageId, last.State);
                }

                if (last.State.IsTerminal())
                {
                    return last.State switch
                    {
                        GenieMessageState.Failed => throw new GenieException(
                            GenieFailureKind.MessageFailed,
                            wire.Error?.Error is { Length: > 0 } e
                                ? $"Genie could not answer: {e}"
                                : "Genie could not answer the question.",
                            errorCode: wire.Error?.Type,
                            lastKnownResponse: last),

                        GenieMessageState.Cancelled => throw new GenieException(
                            GenieFailureKind.MessageCancelled,
                            "The Genie message was cancelled.",
                            lastKnownResponse: last),

                        _ => last,
                    };
                }

                var remaining = timeout - elapsed;
                var delay = interval <= remaining ? interval : remaining;
                await Task.Delay(delay, _time, pollingCancellation.Token).ConfigureAwait(false);

                // Back off gently. Genie questions are usually seconds, occasionally minutes; a
                // fixed 1s interval is wasteful for the long tail and a fixed 5s makes the common
                // case feel sluggish.
                interval = TimeSpan.FromMilliseconds(
                    Math.Min(interval.TotalMilliseconds * 1.5, _options.MaxPollInterval.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw PollingTimedOut(timeout, lastState, last);
        }
    }

    private static GenieException PollingTimedOut(
        TimeSpan timeout,
        GenieMessageState? lastState,
        GenieResponse? last) =>
        new(
            GenieFailureKind.PollingTimeout,
            $"Genie did not finish within {timeout.TotalSeconds:F0}s (last state: {lastState?.ToString() ?? "none"}). " +
            "The question may still complete; it can be re-read with its message id.",
            lastKnownResponse: last);

    public Task<GenieQueryResult?> GetQueryResultAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
        => FetchQueryResultAsync(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/attachments/{Esc(attachmentId)}/query-result",
            HttpMethod.Get, cancellationToken);

    public async Task<GenieQueryResult?> ReExecuteQueryAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
    {
        // execute-query only *starts* the re-execution. Observed against a live workspace it
        // answers HTTP 200 with state PENDING and no manifest, and the rows appear on the
        // ordinary query-result endpoint a moment later. Returning that first response hands the
        // caller nothing while the warehouse work they just paid for completes and is discarded —
        // which surfaced as `export last` telling people to ask the question again.
        var started = await FetchQueryResultAsync(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/attachments/{Esc(attachmentId)}/execute-query",
            HttpMethod.Post,
            cancellationToken).ConfigureAwait(false);

        if (started is not null)
        {
            return started;
        }

        var deadline = _time.GetTimestamp();
        var interval = _options.InitialPollInterval;

        while (_time.GetElapsedTime(deadline) < _options.PollingTimeout)
        {
            await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);

            var result = await GetQueryResultAsync(
                agentId, conversationId, messageId, attachmentId, cancellationToken).ConfigureAwait(false);

            if (result is not null)
            {
                return result;
            }

            interval = TimeSpan.FromMilliseconds(
                Math.Min(interval.TotalMilliseconds * 1.5, _options.MaxPollInterval.TotalMilliseconds));
        }

        // The caller gets null rather than an exception, exactly as before: a re-execution that
        // never lands is a missing result, not a transport failure.
        LogReExecuteTimedOut(_logger, _options.PollingTimeout.TotalSeconds);
        return null;
    }


    public async Task SendFeedbackAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieFeedbackRating rating,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        // Databricks rejects a comment alongside a NONE rating with an HTTP 400 whose message is
        // easy to misread as a transport problem. The constraint is undocumented and was found by
        // calling the endpoint; catching it here turns a confusing server error into a clear one,
        // and costs a round trip nobody wanted.
        if (rating == GenieFeedbackRating.None && !string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException(
                "Databricks only accepts a feedback comment alongside a positive or negative rating. " +
                "Either choose a rating, or send the rating without a comment.",
                nameof(comment));
        }

        var body = new FeedbackRequestWire
        {
            Rating = rating switch
            {
                GenieFeedbackRating.Positive => "POSITIVE",
                GenieFeedbackRating.Negative => "NEGATIVE",
                GenieFeedbackRating.None => "NONE",
                _ => throw new ArgumentOutOfRangeException(nameof(rating)),
            },
            Comment = comment,
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/feedback",
            JsonContent.Create(body, options: Json),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenieResponse> AskAsync(
        string agentId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAskOptions(options);
        var (conversation, messageId) =
            await StartConversationAsync(agentId, question, cancellationToken).ConfigureAwait(false);

        return await CompleteAsync(agentId, conversation.ConversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GenieResponse> FollowUpAsync(
        string agentId,
        string conversationId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAskOptions(options);
        var messageId = await SendMessageAsync(agentId, conversationId, question, cancellationToken)
            .ConfigureAwait(false);

        return await CompleteAsync(agentId, conversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GenieResponse> CompleteAsync(
        string agentId, string conversationId, string messageId,
        GenieAskOptions? options, CancellationToken cancellationToken)
    {
        options ??= new GenieAskOptions();

        var response = await WaitForResponseAsync(agentId, conversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);

        if (!options.IncludeQueryResult || response.Query is null || response.Metadata.AttachmentId is null)
        {
            return response;
        }

        // A missing result must not discard a good answer: the text is the primary payload and
        // the table is an enrichment. Expired results are reported through State, which the
        // caller can act on by re-executing.
        if (response.State == GenieMessageState.QueryResultExpired)
        {
            return response;
        }

        var result = await GetQueryResultAsync(
            agentId, conversationId, messageId, response.Metadata.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        return response with { Result = result };
    }

    private static void ValidateAskOptions(GenieAskOptions? options)
    {
        if (options?.Timeout is { } timeout)
        {
            GenieClientOptions.ValidateWaitTimeout(timeout, nameof(options.Timeout));
        }
    }

    private async Task<GenieQueryResult?> FetchQueryResultAsync(
        string url, HttpMethod method, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, url, content: null, cancellationToken).ConfigureAwait(false);

        var wire = await ReadAsync<QueryResultWire>(response, cancellationToken).ConfigureAwait(false);
        var statement = wire.StatementResponse;
        if (statement?.Manifest?.Schema?.Columns is not { } columns)
        {
            return null;
        }

        var cols = columns
            .Select(c => new GenieColumn(c.Name ?? string.Empty, c.TypeText ?? c.TypeName, c.TypeName))
            .ToList();

        RejectExternalLinks(statement.Result);

        var rows = new List<IReadOnlyList<string?>>(statement.Result?.DataArray ?? []);
        var incomplete = await AppendRemainingChunksAsync(
                rows, statement.Result, statement.StatementId, cancellationToken)
            .ConfigureAwait(false);

        // `manifest.truncated` alone does NOT mean the caller has every row: it reports
        // statement-level truncation by Databricks, and is false for a result that was merely
        // split into chunks.
        //
        // shortOfTotal is not a second guard on the loop stopping early — every such exit already
        // returns true above. It catches the case the loop cannot see: a walk that ran to a clean
        // finish, with no further chunk advertised, yet still ended up with fewer rows than the
        // manifest promised. That is Databricks disagreeing with itself, and the caller should
        // hear about it rather than receive a short result labelled whole.
        var shortOfTotal = statement.Manifest.TotalRowCount is { } total && rows.Count < total;

        return new GenieQueryResult(
            cols,
            rows,
            (statement.Manifest.Truncated ?? false) || incomplete || shortOfTotal,
            statement.Manifest.TotalRowCount);
    }

    /// <summary>
    /// Follows <c>next_chunk_internal_link</c> until the result is complete, appending each
    /// chunk's rows. Returns <see langword="true"/> if rows are known to be missing.
    /// </summary>
    /// <remarks>
    /// Stopping early is never silent: every exit that leaves rows behind returns
    /// <see langword="true"/>, which the caller turns into <c>IsTruncated</c>. A chunk that
    /// cannot be fetched degrades to the rows already in hand rather than discarding a good
    /// partial answer, because the text of the answer is the primary payload.
    /// </remarks>
    private async Task<bool> AppendRemainingChunksAsync(
        List<IReadOnlyList<string?>> rows,
        ResultDataWire? firstChunk,
        string? statementId,
        CancellationToken cancellationToken)
    {
        var chunk = firstChunk;
        var seenLinks = new HashSet<string>(StringComparer.Ordinal);
        var fetches = 0;

        // Driven by next_chunk_index, not by the link: the index is what says rows are missing.
        // Every way of failing to follow it below still returns true, so a result is never
        // reported complete because the means of completing it was unavailable.
        while (chunk?.NextChunkIndex is not null)
        {
            if (rows.Count >= _options.MaxResultRows)
            {
                LogChunkCapReached(_logger, rows.Count, _options.MaxResultRows);
                return true;
            }

            // Neither the row cap nor the repeated-link guard stops a response that hands back a
            // fresh link every time while carrying no rows: nothing grows toward MaxResultRows,
            // and no link repeats. That is an unbounded request flood at a real workspace, so the
            // number of fetches is bounded outright.
            if (++fetches > MaxChunkFetches)
            {
                LogChunkFetchLimitReached(_logger, MaxChunkFetches);
                return true;
            }

            var link = chunk.NextChunkInternalLink;
            if (link is not { Length: > 0 })
            {
                // Observed on the Genie query-result endpoint: it retains the statement id and
                // next index but omits the link that the SQL Statement endpoint returns. That
                // endpoint's path is documented. Uri normalizes dot-only segments even after
                // escaping, so those malformed ids cannot safely address a statement endpoint.
                if (statementId is not { Length: > 0 } || statementId is "." or "..")
                {
                    LogChunkLinkMissing(_logger);
                    return true;
                }

                link = $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/result/chunks/" +
                    chunk.NextChunkIndex.Value.ToString(CultureInfo.InvariantCulture);
            }

            // A chunk that points at itself would otherwise spin here until the process is
            // killed — and a chunk carrying no rows would not even grow toward MaxResultRows.
            // Same failure the agent-listing loop guards against with its repeated page token.
            if (!seenLinks.Add(link))
            {
                LogChunkLinkRepeated(_logger);
                return true;
            }

            if (!ResolvesToWorkspace(link, out var chunkUri))
            {
                // The link is server-supplied and the bearer token rides on every workspace
                // request, so a link naming another host would disclose the token to it. The
                // link is not logged: its contents are attacker-chosen in exactly this case.
                LogChunkLinkRejected(_logger);
                return true;
            }

            try
            {
                chunk = await GetAsync<ResultDataWire>(chunkUri.AbsoluteUri, cancellationToken)
                    .ConfigureAwait(false);

                // Inside the try on purpose. Refusing external links is right for the *first*
                // chunk, where there is nothing to hand back — but here rows and an answer are
                // already in hand, and throwing would discard both. Short and flagged beats
                // losing the answer text, which is the primary payload.
                RejectExternalLinks(chunk);
            }
            catch (GenieException ex)
            {
                // Genie executes the statement; whether the caller may read its remaining chunks
                // is a workspace permission question this client cannot settle in advance.
                LogChunkFetchFailed(_logger, ex.Kind);
                return true;
            }

            rows.AddRange(chunk.DataArray ?? []);
        }

        return false;
    }

    /// <summary>
    /// EXTERNAL_LINKS disposition puts rows behind presigned URLs and omits <c>data_array</c>.
    /// Returning an empty row set as a successful complete result would be the worst outcome
    /// available — a silently empty export that reads as a real answer.
    /// </summary>
    private static void RejectExternalLinks(ResultDataWire? result)
    {
        if (result?.ExternalLinks is not { Count: > 0 } links || result.DataArray is not null)
        {
            return;
        }

        var rowsBehindLinks = links.Sum(l => l.RowCount ?? 0);
        throw new GenieException(
            GenieFailureKind.UnsupportedResult,
            $"Databricks returned this result as {links.Count} external link(s) covering " +
            $"{rowsBehindLinks} row(s) rather than inline rows. This version cannot read that " +
            "form, and will not report an empty result as a complete one. Narrow the question " +
            "so the result comes back inline.");
    }

    /// <summary>
    /// Resolves a server-supplied chunk link against the workspace and accepts it only if it
    /// still points at the workspace.
    /// </summary>
    /// <remarks>
    /// Validating the resolved host rather than the shape of the string is what makes this
    /// robust: <c>//evil.example.com/x</c> looks like a path and resolves to another host, and
    /// no amount of prefix-checking is provably free of the next such quirk. Databricks
    /// documents the link as opaque, so its shape is not ours to constrain — where it ends up is.
    /// </remarks>
    private bool ResolvesToWorkspace(string link, [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;
        var workspace = _http.BaseAddress ?? _options.Host!;

        if (!Uri.TryCreate(workspace, link, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme != workspace.Scheme
            || !candidate.Host.Equals(workspace.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != workspace.Port)
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static GenieResponse Normalize(
        MessageWire wire, string agentId, string conversationId, string messageId,
        TimeSpan duration, int pollCount)
    {
        var attachments = wire.Attachments ?? [];

        // Genie can emit several text attachments; intermediate "thinking" phases are working
        // notes, not the answer. Joining every text blob would show the user Genie's scratchpad.
        var answer = attachments
            .Select(a => a.Text)
            .Where(t => t?.Content is { Length: > 0 }
                        && t.Purpose != "FOLLOW_UP_QUESTION"
                        && t.Phase != "RESPONSE_PHASE_THINKING")
            .Select(t => t!.Content!)
            .LastOrDefault();

        var queryAttachment = attachments.FirstOrDefault(a => a.Query is not null);

        var query = queryAttachment?.Query is { } q
            ? new GenieQuery(
                q.Query,
                q.Title,
                q.Description,
                q.StatementId,
                q.Parameters?.Select(p => new GenieQueryParameter(p.Keyword, p.SqlType, p.Value)).ToList())
            : null;

        var suggested = attachments
            .SelectMany(a => a.SuggestedQuestions?.Questions ?? [])
            .ToList();

        return new GenieResponse(
            agentId,
            conversationId,
            messageId,
            GenieMessageStateExtensions.FromWire(wire.Status),
            answer,
            query,
            Result: null,
            suggested,
            new GenieResponseMetadata(
                duration,
                pollCount,
                FromUnixTimestamp(wire.CreatedTimestamp),
                FromUnixTimestamp(wire.LastUpdatedTimestamp),
                queryAttachment?.AttachmentId))
        {
            HasVisualization = attachments.Any(a => a.Viz is not null),
        };
    }

    /// <summary>
    /// Converts a Genie timestamp, detecting its unit by magnitude.
    /// </summary>
    /// <remarks>
    /// The field is typed <c>int64</c> and its unit is undocumented — the SDK name suggests
    /// milliseconds, while the one published example is ten digits, which is seconds. Assuming
    /// milliseconds on a seconds value yields January 1970 instead of the real date: a silent
    /// 55-year error on a public property. The threshold is the year 2001 in milliseconds; any
    /// plausible Genie timestamp in seconds is far below it, and any in milliseconds far above.
    /// </remarks>
    private static DateTimeOffset? FromUnixTimestamp(long? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        const long millisecondThreshold = 100_000_000_000L;

        return value.Value >= millisecondThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
            : DateTimeOffset.FromUnixTimeSeconds(value.Value);
    }

    private static string Esc(string segment) => Uri.EscapeDataString(segment);

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, url, JsonContent.Create(body, options: Json), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TOut>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new GenieException(
                GenieFailureKind.Network,
                "The request to Databricks exceeded the configured resilience timeout.",
                innerException: ex);
        }
        catch (BrokenCircuitException ex)
        {
            throw new GenieException(
                GenieFailureKind.Network,
                "Requests to Databricks are temporarily paused after repeated transient failures.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GenieException(
                GenieFailureKind.Network, $"Could not reach the Databricks workspace: {ex.Message}",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GenieException(
                GenieFailureKind.Network, "The request to Databricks timed out.", innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ToFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<GenieException> ToFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // The Genie error body shape is not documented, so parsing is best-effort and the
        // status code remains the source of truth for how to classify the failure.
        ApiErrorWire? error = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                error = JsonSerializer.Deserialize<ApiErrorWire>(body, Json);
            }
        }
        catch (JsonException)
        {
            // Not JSON. The status code below still classifies it.
        }

        var status = (int)response.StatusCode;
        var detail = error?.Message is { Length: > 0 } m ? $": {m}" : ".";

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GenieFailureKind.Authentication,
            HttpStatusCode.Forbidden => GenieFailureKind.Authorization,
            HttpStatusCode.NotFound => GenieFailureKind.AgentNotFound,
            HttpStatusCode.TooManyRequests => GenieFailureKind.RateLimited,
            _ => GenieFailureKind.Unexpected,
        };

        var message = kind switch
        {
            GenieFailureKind.Authentication =>
                $"Databricks rejected the credentials (HTTP 401){detail} The OAuth token may have expired; " +
                "try `databricks auth login --profile <profile>`.",
            GenieFailureKind.Authorization =>
                $"The authenticated identity is not permitted to do this (HTTP 403){detail} " +
                "Genie Agent access and the underlying Unity Catalog grants are managed in Databricks.",
            GenieFailureKind.AgentNotFound =>
                $"Not found (HTTP 404){detail} The Agent, conversation or message may not exist, " +
                "or may be in a different workspace than the configured profile.",
            GenieFailureKind.RateLimited =>
                $"Databricks is rate limiting this client (HTTP 429){detail}",
            _ => $"Databricks returned HTTP {status}{detail}",
        };

        return new GenieException(kind, message, status, error?.ErrorCode);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Agent listing returned a repeated page token; stopping pagination.")]
    private static partial void LogRepeatedPageToken(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Genie message {MessageId} entered state {State}.")]
    private static partial void LogStateChanged(ILogger logger, string messageId, GenieMessageState state);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Stopped after {Rows} rows, at the MaxResultRows limit of {Limit}. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkCapReached(ILogger logger, int rows, int limit);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Refused a next-chunk link that was not a workspace-relative path. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkRejected(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "A result chunk advertised a further chunk but carried no link to it. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkMissing(ILogger logger);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "A result chunk link repeated one already followed; stopping to avoid looping. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkRepeated(ILogger logger);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Stopped after {Limit} chunk requests for a single result. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkFetchLimitReached(ILogger logger, int limit);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "A re-executed query did not produce a result within {Seconds}s.")]
    private static partial void LogReExecuteTimedOut(ILogger logger, double seconds);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Could not fetch a further result chunk ({Kind}). " +
                  "The rows already retrieved are returned and reported as truncated.")]
    private static partial void LogChunkFetchFailed(ILogger logger, GenieFailureKind kind);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);

            return value ?? throw new GenieException(
                GenieFailureKind.MalformedResponse, "Databricks returned an empty response body.");
        }
        catch (JsonException ex)
        {
            // The body is deliberately not included: it can contain query results, which are
            // governed data, and this message may reach a log or a bug report.
            throw new GenieException(
                GenieFailureKind.MalformedResponse,
                "Databricks returned a response this client could not parse.",
                innerException: ex);
        }
    }
}
