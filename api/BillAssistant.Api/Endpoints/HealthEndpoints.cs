using BillAssistant.Core.Abstractions;
using BillAssistant.Infrastructure.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace BillAssistant.Api.Endpoints;

/// <summary>
/// Reports whether the API can actually reach its two backing services.
/// </summary>
/// <remarks>
/// Deliberately does real work - an embedding round-trip and a vector-store call - because "the
/// process is up" is not the failure people hit. The failure they hit is Ollama not running or the
/// model not pulled, and this says which.
/// </remarks>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            IEmbeddingGenerator<string, Embedding<float>> embeddings,
            IBillChunkStore chunkStore,
            IOptions<AiOptions> aiOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Health");
            var ai = aiOptions.Value;

            var (embeddingOk, embeddingError, dimensions) = await CheckEmbeddingsAsync(embeddings, logger, ct);
            var (vectorOk, vectorError) = await CheckVectorStoreAsync(chunkStore, logger, ct);

            var expected = ai.Active.EmbeddingDimensions;
            var dimensionsMatch = dimensions is null || dimensions == expected;

            var healthy = embeddingOk && vectorOk && dimensionsMatch;

            var body = new
            {
                status = healthy ? "healthy" : "degraded",
                provider = ai.Provider,
                chatModel = ai.Active.ChatModel,
                embeddingModel = ai.Active.EmbeddingModel,
                collection = chunkStore.CollectionName,
                checks = new
                {
                    models = new { ok = embeddingOk, error = embeddingError, dimensions },
                    vectorStore = new { ok = vectorOk, error = vectorError },
                    dimensions = new
                    {
                        ok = dimensionsMatch,
                        expected,
                        actual = dimensions,
                        error = dimensionsMatch
                            ? null
                            : $"The embedding model returns {dimensions} dimensions but the collection is configured for {expected}. Re-ingest into a fresh collection."
                    }
                }
            };

            return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithTags("Health")
        .WithName("Health");

        return app;
    }

    private static async Task<(bool Ok, string? Error, int? Dimensions)> CheckEmbeddingsAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var vector = await embeddings.GenerateVectorAsync("health check", cancellationToken: ct);
            return (true, null, vector.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check: the embedding model is unreachable.");
            return (false, Describe(ex), null);
        }
    }

    private static async Task<(bool Ok, string? Error)> CheckVectorStoreAsync(
        IBillChunkStore chunkStore,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await chunkStore.EnsureReadyAsync(ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check: the vector store is unreachable.");
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
}
