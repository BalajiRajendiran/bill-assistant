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

        return new ChatAnswer(response.Text.Trim(), context.Citations, context.Totals);
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

        await foreach (var update in chatClient.GetStreamingResponseAsync(context.Messages, ChatSettings, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return ChatStreamEvent.Token(update.Text);
            }
        }

        yield return ChatStreamEvent.Done();
    }

    private static ChatOptions ChatSettings => new()
    {
        // Low but not zero: answers should be stable without reading like a template.
        Temperature = 0.2f,
        MaxOutputTokens = 600
    };

    private async Task<RetrievalContext> BuildContextAsync(ChatRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var intent = QuestionAnalyzer.Analyse(request.Question, today);

        // An explicit filter from the caller always wins over one inferred from the wording.
        var utility = request.Utility ?? intent.Utility;
        var from = request.From ?? intent.From;
        var to = request.To ?? intent.To;
        var top = Math.Clamp(request.TopK, 1, MaxTopK);

        var queryEmbedding = await embeddingGenerator.GenerateVectorAsync(request.Question, cancellationToken: ct);

        var chunks = await chunkStore.SearchAsync(queryEmbedding, top, new ChunkFilter(utility, from, to), ct);

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

                chunks = await chunkStore.SearchAsync(queryEmbedding, top, new ChunkFilter(utility, shiftedFrom, shiftedTo), ct);

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
            totals = await repository.ComputeTotalsAsync(new BillQuery(utility, from, to), ct);
        }

        logger.LogInformation(
            "Question matched {Count} chunk(s) (utility {Utility}, {From} to {To}, totals: {Totals}).",
            chunks.Count, utility, from, to, totals is not null);

        var citations = chunks
            .Select(c => new Citation(
                Guid.TryParse(c.Chunk.BillId, out var billId) ? billId : Guid.Empty,
                c.Chunk.FileName,
                c.Chunk.PageNumber,
                Snippet(c.Chunk.Text),
                c.Chunk.Utility,
                c.Score))
            .ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BillPrompts.AnswerSystem),
            new(ChatRole.User, BillPrompts.AnswerUser(request.Question, chunks, totals))
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
