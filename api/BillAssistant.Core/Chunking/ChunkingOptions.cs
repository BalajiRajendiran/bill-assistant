namespace BillAssistant.Core.Chunking;

/// <summary>Tuning for <see cref="BillChunker"/>.</summary>
public sealed class ChunkingOptions
{
    /// <summary>Soft upper bound on characters per chunk. A single oversized line may exceed it.</summary>
    public int MaxChars { get; set; } = 1200;

    /// <summary>Characters of trailing context carried into the next chunk, snapped to line boundaries.</summary>
    public int OverlapChars { get; set; } = 200;

    /// <summary>Chunks shorter than this are folded into the previous chunk rather than indexed alone.</summary>
    public int MinChars { get; set; } = 80;

    public void Validate()
    {
        if (MaxChars < 200)
            throw new ArgumentOutOfRangeException(nameof(MaxChars), MaxChars, "MaxChars must be at least 200.");
        if (OverlapChars < 0 || OverlapChars >= MaxChars / 2)
            throw new ArgumentOutOfRangeException(nameof(OverlapChars), OverlapChars, "OverlapChars must be non-negative and less than half of MaxChars.");
        if (MinChars < 0 || MinChars >= MaxChars)
            throw new ArgumentOutOfRangeException(nameof(MinChars), MinChars, "MinChars must be non-negative and less than MaxChars.");
    }
}
