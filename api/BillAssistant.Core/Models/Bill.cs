namespace BillAssistant.Core.Models;

/// <summary>
/// A single ingested bill. Persisted relationally (SQLite) so that numeric questions - totals,
/// trends, "what is due next" - are answered with SQL rather than by asking the model to do
/// arithmetic over retrieved text.
/// </summary>
public class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Name the file was uploaded under.</summary>
    public required string FileName { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    public int PageCount { get; set; }

    public int ChunkCount { get; set; }

    public MetadataStatus MetadataStatus { get; set; } = MetadataStatus.Pending;

    /// <summary>Why metadata was rejected, when <see cref="MetadataStatus"/> is NeedsReview.</summary>
    public string? MetadataNotes { get; set; }

    // --- extracted metadata ---

    public UtilityKind Utility { get; set; } = UtilityKind.Unknown;

    public string? ProviderName { get; set; }

    /// <summary>Stored redacted - only the last four characters are kept.</summary>
    public string? AccountNumber { get; set; }

    public DateOnly? PeriodStart { get; set; }

    public DateOnly? PeriodEnd { get; set; }

    /// <summary>
    /// The total due, in minor units (cents). Money is stored as an integer because SQLite has no
    /// decimal type - EF maps decimal to TEXT, which cannot be summed in SQL. Keeping cents makes the
    /// aggregates on this table both exact and translatable to SQL.
    /// </summary>
    public long? AmountDueMinor { get; set; }

    /// <summary>Convenience view over <see cref="AmountDueMinor"/>. Not mapped; do not query on it.</summary>
    public decimal? AmountDue
    {
        get => AmountDueMinor is { } minor ? minor / 100m : null;
        set => AmountDueMinor = value is { } amount ? (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero) : null;
    }

    public string? Currency { get; set; }

    public DateOnly? DueDate { get; set; }

    public double? UsageQuantity { get; set; }

    /// <summary>kWh, therms, gallons, GB...</summary>
    public string? UsageUnit { get; set; }

    public string PeriodLabel => (PeriodStart, PeriodEnd) switch
    {
        ({ } s, { } e) => $"{s:yyyy-MM-dd} to {e:yyyy-MM-dd}",
        ({ } s, null) => $"from {s:yyyy-MM-dd}",
        (null, { } e) => $"to {e:yyyy-MM-dd}",
        _ => "unknown period"
    };
}
