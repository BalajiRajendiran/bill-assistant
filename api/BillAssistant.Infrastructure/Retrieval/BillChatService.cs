using System.Runtime.CompilerServices;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Prompts;
using BillAssistant.Core.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatRequest = BillAssistant.Core.Models.ChatRequest;

namespace BillAssistant.Infrastructure.Retrieval;

/// <summary>
/// Answers questions about the household's bills: retrieve, ground, answer.
/// </summary>
/// <remarks>
/// Numeric questions get a <see cref="BillTotals"/> block computed in SQL alongside the retrieved
/// excerpts, and the system prompt tells the model to prefer those figures. Retrieval finds the right
/// passages; it should not be asked to do arithmetic.
/// </remarks>
public sealed class BillChatService(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IBillChunkStore chunkStore,
    IBillRepository repository,
    TimeProvider timeProvider,
    ILogger<BillChatService> logger) : IBillChatService
{
    /// <summary>
    /// Kept deliberately small. Every excerpt costs context, and with Ollama's 4096-token default an
    /// over-long prompt loses its system instructions before it loses its excerpts.
    /// </summary>
    private const int MaxTopK = 12;

    public async Task<ChatAnswer> AskAsync(ChatRequest request, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(request, ct);

        if (context.Chunks.Count == 0)
        {
            return new ChatAnswer(NothingFoundMessage(context.Totals), [], context.Totals);
        }

        var response = await chatClient.GetResponseAsync(context.Messages, ChatSettings, ct);

        return new ChatAnswer(AnswerText.Clean(response.Text), context.Citations, context.Totals);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        RetrievalContext? context = null;
        string? failure = null;

        try
        {
            context = await BuildContextAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A stream has already begun from the caller's point of view, so a failure has to be
            // reported as an event rather than thrown.
            logger.LogError(ex, "Retrieval failed for question: {Question}", request.Question);
            failure = "Something went wrong while searching your bills.";
        }

        if (context is null)
        {
            yield return ChatStreamEvent.Error(failure ?? "Retrieval failed.");
            yield break;
        }

        // Sources first: the UI can render citation chips while the answer is still being written.
        yield return ChatStreamEvent.Sources(context.Citations, context.Totals);

        if (context.Chunks.Count == 0)
        {
            yield return ChatStreamEvent.Token(NothingFoundMessage(context.Totals));
            yield return ChatStreamEvent.Done();
            yield break;
        }

        // The same scaffolding guard as the non-streaming path, applied token by token: a tag can
        // straddle two updates, so the filter holds back anything that might still become one.
        var filter = new AnswerText.Filter();

        await foreach (var update in chatClient.GetStreamingResponseAsync(context.Messages, ChatSettings, ct))
        {
            if (string.IsNullOrEmpty(update.Text))
            {
                continue;
            }

            var safe = filter.Push(update.Text);
            if (safe.Length > 0)
            {
                yield return ChatStreamEvent.Token(safe);
            }
        }

        var tail = filter.Flush();
        if (tail.Length > 0)
        {
            yield return ChatStreamEvent.Token(tail);
        }

        yield return ChatStreamEvent.Done();
    }

    /// <summary>How many earlier exchanges are shown when resolving a follow-up.</summary>
    private const int MaxHistoryExchanges = 4;

    /// <summary>
    /// Longest rewrite we will trust. A model that ignores the instruction and answers the question
    /// instead produces a paragraph, and searching on that would be worse than searching on the
    /// pronoun - so anything this long is discarded in favour of what the user actually typed.
    /// </summary>
    private const int MaxRewriteChars = 300;

    private static ChatOptions ChatSettings => new()
    {
        // Low but not zero: answers should be stable without reading like a template.
        Temperature = 0.2f,
        MaxOutputTokens = 600
    };

    /// <summary>Resolving a reference is mechanical, so it is deterministic and tightly capped.</summary>
    private static ChatOptions RewriteSettings => new()
    {
        Temperature = 0f,
        MaxOutputTokens = 120
    };

    /// <summary>
    /// Turns a follow-up into a question that stands on its own, so that retrieval has something to
    /// match against. Falls back to the question as typed whenever that cannot be done safely - this
    /// step is an improvement to retrieval, never a precondition for answering.
    /// </summary>
    private async Task<string> ResolveQuestionAsync(ChatRequest request, CancellationToken ct)
    {
        if (request.History is not { Count: > 0 } history)
        {
            return request.Question;
        }

        // A question that already stands on its own is left exactly as it is - see LooksLikeFollowUp
        // for why sending it to the model anyway is actively harmful.
        if (!QuestionAnalyzer.LooksLikeFollowUp(request.Question))
        {
            return request.Question;
        }

        var recent = history.Count <= MaxHistoryExchanges
            ? history
            : [.. history.Skip(history.Count - MaxHistoryExchanges)];

        try
        {
            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BillPrompts.RewriteSystem),
                    new ChatMessage(ChatRole.User, BillPrompts.RewriteUser(recent, request.Question))
                ],
                RewriteSettings,
                ct);

            var rewritten = response.Text.Trim().Trim('"').Trim();

            if (rewritten.Length == 0 || rewritten.Length > MaxRewriteChars)
            {
                return request.Question;
            }

            if (!string.Equals(rewritten, request.Question, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Follow-up resolved to: {Resolved}", rewritten);
            }

            return rewritten;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve the follow-up; searching on it as typed.");
            return request.Question;
        }
    }

    private async Task<RetrievalContext> BuildContextAsync(ChatRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Everything downstream - the embedding, the utility filter, the date window - is derived
        // from the resolved question, because that is the one that says what was meant.
        var searchQuestion = await ResolveQuestionAsync(request, ct);
        var intent = QuestionAnalyzer.Analyse(searchQuestion, today);

        // An explicit filter from the caller always wins over one inferred from the wording.
        var kinds = request.Utility is { } chosen ? [chosen] : intent.Utilities;
        var from = request.From ?? intent.From;
        var to = request.To ?? intent.To;
        var top = Math.Clamp(request.TopK, 1, MaxTopK);

        var queryEmbedding = await embeddingGenerator.GenerateVectorAsync(searchQuestion, cancellationToken: ct);

        var chunks = await chunkStore.SearchAsync(queryEmbedding, top, new ChunkFilter(Utilities: kinds, From: from, To: to), ct);

        // "in July" with no year is taken to mean the most recent July, but the most recent July may
        // hold no bills - a household that started tracking last year has none for this one. When the
        // year was a guess and the guess found nothing, step back a year before giving up. An explicit
        // filter from the caller is never second-guessed this way.
        if (chunks.Count == 0 && intent.YearWasGuessed && request.From is null && request.To is null)
        {
            for (var yearsBack = 1; yearsBack <= 2 && chunks.Count == 0; yearsBack++)
            {
                var shiftedFrom = intent.From?.AddYears(-yearsBack);
                var shiftedTo = intent.To?.AddYears(-yearsBack);

                chunks = await chunkStore.SearchAsync(queryEmbedding, top, new ChunkFilter(Utilities: kinds, From: shiftedFrom, To: shiftedTo), ct);

                if (chunks.Count > 0)
                {
                    logger.LogInformation(
                        "No bills in the assumed window; answered from {From} to {To} instead.", shiftedFrom, shiftedTo);
                    from = shiftedFrom;
                    to = shiftedTo;
                }
            }
        }

        BillTotals? totals = null;
        if (intent.WantsTotals)
        {
            totals = await repository.ComputeTotalsAsync(new BillQuery(From: from, To: to, Utilities: kinds), ct);
        }

        logger.LogInformation(
            "Question matched {Count} chunk(s) (utilities {Utilities}, {From} to {To}, totals: {Totals}).",
            chunks.Count, string.Join(" + ", kinds), from, to, totals is not null);

        // One page of one bill is one source, however many of its chunks matched. Without this the
        // same file and page is listed two or three times over, which reads as corroboration from
        // separate bills when it is the same passage retrieved twice.
        var citations = chunks
            .Select(c => new Citation(
                Guid.TryParse(c.Chunk.BillId, out var billId) ? billId : Guid.Empty,
                c.Chunk.FileName,
                c.Chunk.PageNumber,
                Snippet(c.Chunk.Text),
                c.Chunk.Utility,
                c.Score))
            .GroupBy(c => (c.BillId, c.PageNumber))
            .Select(g => g.MaxBy(c => c.Score ?? double.MinValue)!)
            .ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BillPrompts.AnswerSystem),
            new(ChatRole.User, BillPrompts.AnswerUser(
                request.Question,
                chunks,
                totals,
                today,
                string.Equals(searchQuestion, request.Question, StringComparison.Ordinal) ? null : searchQuestion))
        };

        return new RetrievalContext(chunks, citations, totals, messages);
    }

    private static string NothingFoundMessage(BillTotals? totals) =>
        totals is not null
            ? $"I could not find a passage about that, but {totals.BillCount} matching bill(s) total {totals.TotalAmount:0.00} {totals.Currency}."
            : "I could not find anything about that in the bills you have uploaded. If the bill is missing, upload it and ask again.";

    private static string Snippet(string text)
    {
        const int max = 320;
        var flattened = text.Replace('\n', ' ').Trim();
        return flattened.Length <= max ? flattened : flattened[..max] + "...";
    }

    private sealed record RetrievalContext(
        IReadOnlyList<ScoredChunk> Chunks,
        IReadOnlyList<Citation> Citations,
        BillTotals? Totals,
        List<ChatMessage> Messages);
}
