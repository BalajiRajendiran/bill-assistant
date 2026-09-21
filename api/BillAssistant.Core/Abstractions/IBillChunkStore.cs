using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>A chunk returned by a vector search, with its similarity score.</summary>
public sealed record ScoredChunk(BillChunk Chunk, double? Score);

/// <summary>Pre-filter applied before the vector search runs.</summary>
/// <param name="Utility">The single kind to narrow to, when there is one.</param>
/// <param name="Utilities">
/// Several kinds, for a question that named more than one. Takes precedence over
/// <paramref name="Utility"/>; use <see cref="ChunkFilter.Kinds"/> rather than reading either.
/// </param>
public sealed record ChunkFilter(
    UtilityKind? Utility = null,
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyList<UtilityKind>? Utilities = null)
{
    /// <summary>The kinds this filter covers, empty for "every kind".</summary>
    public IReadOnlyList<UtilityKind> Kinds =>
        Utilities is { Count: > 0 } many ? many : Utility is { } one ? [one] : [];
}

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
