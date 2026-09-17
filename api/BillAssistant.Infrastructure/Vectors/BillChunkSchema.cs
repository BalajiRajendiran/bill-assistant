using BillAssistant.Core.Models;
using Microsoft.Extensions.VectorData;

namespace BillAssistant.Infrastructure.Vectors;

/// <summary>
/// Builds the vector collection model for <see cref="BillChunk"/> at runtime.
/// </summary>
/// <remarks>
/// The schema is built in code rather than declared with [VectorStoreVector] attributes because the
/// embedding dimension is configuration, not a constant: nomic-embed-text produces 768 dimensions and
/// text-embedding-3-small produces 1536, and attribute arguments must be compile-time constants.
/// </remarks>
public static class BillChunkSchema
{
    public static VectorStoreCollectionDefinition Create(int dimensions) => new()
    {
        Properties =
        [
            // Qdrant point ids must be a Guid or a ulong; string keys are not supported.
            new VectorStoreKeyProperty(nameof(BillChunk.Id), typeof(Guid)),

            // Indexed properties are the ones Qdrant can pre-filter on before the vector search runs.
            new VectorStoreDataProperty(nameof(BillChunk.BillId), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(BillChunk.Utility), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(BillChunk.PeriodStartDay), typeof(int)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(BillChunk.PeriodEndDay), typeof(int)) { IsIndexed = true },

            new VectorStoreDataProperty(nameof(BillChunk.Text), typeof(string)),
            new VectorStoreDataProperty(nameof(BillChunk.PageNumber), typeof(int)),
            new VectorStoreDataProperty(nameof(BillChunk.ChunkIndex), typeof(int)),
            new VectorStoreDataProperty(nameof(BillChunk.FileName), typeof(string)),
            new VectorStoreDataProperty(nameof(BillChunk.ProviderName), typeof(string)),

            new VectorStoreVectorProperty(nameof(BillChunk.Embedding), typeof(ReadOnlyMemory<float>), dimensions)
            {
                DistanceFunction = DistanceFunction.CosineSimilarity,
                IndexKind = IndexKind.Hnsw
            }
        ]
    };

    /// <summary>
    /// Collection name for a given embedding model, e.g. "bill_chunks_nomic_embed_text".
    /// </summary>
    /// <remarks>
    /// The model is part of the name on purpose. Vectors from different models are not comparable, and
    /// one written into another's collection would return plausible-looking nonsense. Naming them apart
    /// turns a provider swap into an obvious "collection not found" instead of a silent accuracy loss.
    /// </remarks>
    public static string CollectionName(string prefix, string embeddingModel)
    {
        var slug = new string([.. embeddingModel.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')]);
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return $"{prefix}_{slug.Trim('_')}";
    }
}
