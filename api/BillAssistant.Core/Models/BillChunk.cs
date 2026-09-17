namespace BillAssistant.Core.Models;

/// <summary>
/// One embedded slice of a bill, stored as a point in Qdrant.
/// </summary>
/// <remarks>
/// Deliberately carries no [VectorStoreKey]/[VectorStoreVector] attributes: the embedding dimension
/// depends on the configured provider (768 for nomic-embed-text, 1536 for text-embedding-3-small) and
/// attribute arguments must be compile-time constants. The collection model is therefore built at
/// runtime - see BillChunkSchema in the Infrastructure project.
///
/// The metadata fields are denormalised copies of the parent bill's, because Qdrant can only pre-filter
/// on what lives in the point payload. Dates are stored as day numbers so range filters translate to
/// plain integer comparisons.
/// </remarks>
public sealed class BillChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The parent bill's id, as a string. Vector-store payloads support only primitive types - a Guid
    /// is valid as a point key but not as a filterable data property - so it is stored in "D" format.
    /// </summary>
    public string BillId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int PageNumber { get; set; }

    public int ChunkIndex { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Stored as a string so filter expressions stay provider-agnostic.</summary>
    public string Utility { get; set; } = nameof(UtilityKind.Unknown);

    public string ProviderName { get; set; } = string.Empty;

    /// <summary><see cref="DateOnly.DayNumber"/> of the period start, or 0 when unknown.</summary>
    public int PeriodStartDay { get; set; }

    /// <summary><see cref="DateOnly.DayNumber"/> of the period end, or 0 when unknown.</summary>
    public int PeriodEndDay { get; set; }

    public ReadOnlyMemory<float> Embedding { get; set; }
}
