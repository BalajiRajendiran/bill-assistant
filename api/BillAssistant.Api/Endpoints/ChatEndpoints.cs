using System.Text.Json;
using BillAssistant.Api.Contracts;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Validation;
using ChatRequest = BillAssistant.Core.Models.ChatRequest;

namespace BillAssistant.Api.Endpoints;

/// <summary>Ask questions about the ingested bills.</summary>
public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat");

        group.MapPost("/", AskAsync)
            .WithName("Ask")
            .WithSummary("Ask a question and get a grounded answer with citations.");

        group.MapPost("/stream", StreamAsync)
            .WithName("AskStreaming")
            .WithSummary("Ask a question and stream the answer as server-sent events.");

        return group;
    }

    private static async Task<IResult> AskAsync(AskRequest request, IBillChatService chat, CancellationToken ct)
    {
        if (!TryBuild(request, out var chatRequest, out var error))
        {
            return TypedResults.BadRequest(new { error });
        }

        var answer = await chat.AskAsync(chatRequest, ct);

        return TypedResults.Ok(new AskResponse(
            answer.Answer,
            [.. answer.Citations.Select(CitationResponse.Create)],
            TotalsResponse.Create(answer.Totals)));
    }

    /// <summary>
    /// Streams the answer as SSE. The first event carries the citations so the UI can show its sources
    /// immediately; tokens follow. Written directly to the response rather than returned as a result,
    /// because the stream must start before the model has finished.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, AskRequest request, IBillChatService chat, CancellationToken ct)
    {
        if (!TryBuild(request, out var chatRequest, out var error))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error }, ct);
            return;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        await foreach (var chunk in chat.StreamAsync(chatRequest, ct))
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = chunk.Type,
                text = chunk.Text,
                citations = chunk.Citations?.Select(CitationResponse.Create),
                totals = TotalsResponse.Create(chunk.Totals)
            }, SerializerOptions);

            await http.Response.WriteAsync($"data: {payload}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static bool TryBuild(AskRequest request, out ChatRequest chatRequest, out string? error)
    {
        chatRequest = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            error = "Ask a question.";
            return false;
        }

        if (request.Question.Length > 1000)
        {
            error = "That question is too long.";
            return false;
        }

        UtilityKind? utility = null;
        if (!string.IsNullOrWhiteSpace(request.Utility))
        {
            var parsed = BillMetadataValidator.ParseUtility(request.Utility);
            if (parsed == UtilityKind.Unknown)
            {
                error = $"Unknown utility '{request.Utility}'.";
                return false;
            }

            utility = parsed;
        }

        // The client carries the conversation, so cap what it may send: history is a hint for
        // resolving a reference, not evidence, and an unbounded one would crowd out the excerpts.
        const int maxHistoryTurns = 8;

        var history = request.History is { Count: > 0 } turns
            ? turns.Where(t => !string.IsNullOrWhiteSpace(t.Question))
                   .TakeLast(maxHistoryTurns)
                   .Select(t => new ChatExchange(t.Question.Trim(), (t.Answer ?? string.Empty).Trim()))
                   .ToList()
            : null;

        chatRequest = new ChatRequest(request.Question.Trim(), utility, request.From, request.To, request.TopK, history);
        return true;
    }
}
