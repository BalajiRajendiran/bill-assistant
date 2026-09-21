using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace BillAssistant.Infrastructure.Vectors;

/// <summary>
/// Chunk storage over any Microsoft.Extensions.VectorData provider - Qdrant in production, the
/// in-memory provider in tests. Named for the abstraction rather than for Qdrant because nothing in
/// here is Qdrant-specific; the provider is chosen where the VectorStore is registered.
/// </summary>
public sealed class VectorDataBillChunkStore : IBillChunkStore
{
    private readonly VectorStoreCollection<Guid, BillChunk> _collection;
    private readonly ILogger<VectorDataBillChunkStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _ready;

    public VectorDataBillChunkStore(
        VectorStore vectorStore,
        string collectionName,
        int embeddingDimensions,
        ILogger<VectorDataBillChunkStore> logger)
    {
        ArgumentNullException.ThrowIfNull(vectorStore);

        CollectionName = collectionName;
        _logger = logger;
        _collection = vectorStore.GetCollection<Guid, BillChunk>(collectionName, BillChunkSchema.Create(embeddingDimensions));
    }

    public string CollectionName { get; }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_ready)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready)
            {
                return;
            }

            await _collection.EnsureCollectionExistsAsync(ct);
            _ready = true;
            _logger.LogInformation("Vector collection {Collection} is ready.", CollectionName);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<BillChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        await EnsureReadyAsync(ct);
        await _collection.UpsertAsync(chunks, ct);
    }

    public async Task DeleteByBillAsync(Guid billId, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);

        // No delete-by-filter in the abstraction, so collect the keys first. A bill is a few dozen
        // chunks at most, so paging this is not worth the complexity.
        var key = billId.ToString();
        var keys = new List<Guid>();
        await foreach (var chunk in _collection.GetAsync(c => c.BillId == key, int.MaxValue, cancellationToken: ct))
        {
            keys.Add(chunk.Id);
        }

        if (keys.Count > 0)
        {
            await _collection.DeleteAsync(keys, ct);
            _logger.LogInformation("Deleted {Count} chunks for bill {BillId}.", keys.Count, billId);
        }
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int top,
        ChunkFilter? filter = null,
        CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);

        var options = new VectorSearchOptions<BillChunk> { Filter = BuildFilter(filter) };
        var results = new List<ScoredChunk>(top);

        await foreach (var result in _collection.SearchAsync(queryEmbedding, top, options, ct))
        {
            results.Add(new ScoredChunk(result.Record, result.Score));
        }

        return results;
    }

    /// <summary>
    /// Translates a domain filter into an expression the provider pushes down into the search.
    /// </summary>
    /// <remarks>
    /// Dates compare as day numbers because integer range filters translate cleanly on every provider.
    /// A chunk with day 0 has no known period; those are left in rather than filtered out, so a bill
    /// whose metadata failed extraction stays findable by text.
    /// </remarks>
    private static System.Linq.Expressions.Expression<Func<BillChunk, bool>>? BuildFilter(ChunkFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        // Matched against the set the question named, so a question about two utilities pre-filters
        // to those two instead of searching every bill. Providers push Contains down as a match-any.
        var kinds = filter.Kinds.Select(k => k.ToString()).ToArray();
        var from = filter.From?.DayNumber ?? 0;
        var to = filter.To?.DayNumber ?? 0;

        return (kinds.Length > 0, from, to) switch
        {
            (false, 0, 0) => null,
            (true, 0, 0) => c => kinds.Contains(c.Utility),
            (false, > 0, 0) => c => c.PeriodEndDay == 0 || c.PeriodEndDay >= from,
            (false, 0, > 0) => c => c.PeriodStartDay == 0 || c.PeriodStartDay <= to,
            (false, > 0, > 0) => c => (c.PeriodEndDay == 0 || c.PeriodEndDay >= from) && (c.PeriodStartDay == 0 || c.PeriodStartDay <= to),
            (true, > 0, 0) => c => kinds.Contains(c.Utility) && (c.PeriodEndDay == 0 || c.PeriodEndDay >= from),
            (true, 0, > 0) => c => kinds.Contains(c.Utility) && (c.PeriodStartDay == 0 || c.PeriodStartDay <= to),
            (true, > 0, > 0) => c => kinds.Contains(c.Utility)
                && (c.PeriodEndDay == 0 || c.PeriodEndDay >= from)
                && (c.PeriodStartDay == 0 || c.PeriodStartDay <= to),
            _ => null
        };
    }
}
