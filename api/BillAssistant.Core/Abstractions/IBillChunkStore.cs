using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>A chunk returned by a vector search, with its similarity score.</summary>
public sealed record ScoredChunk(BillChunk Chunk, double? Score);

/// <summary>Pre-filter applied before the vector search runs.</summary>
public sealed record ChunkFilter(UtilityKind? Utility = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>Vector storage for bill chunks.</summary>
public interface IBillChunkStore
{
    /// <summary>Name of the backing collection; includes the embedding model so a provider swap is visible.</summary>
    string CollectionName { get; }

    Task EnsureReadyAsync(CancellationToken ct = default);

    Task UpsertAsync(IReadOnlyList<BillChunk> chunks, CancellationToken ct = default);

    Task DeleteByBillAsync(Guid billId, CancellationToken ct = default);

    Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int top,
        ChunkFilter? filter = null,
        CancellationToken ct = default);
}
