namespace BillAssistant.Core.Models;

/// <summary>Outcome of ingesting one uploaded PDF.</summary>
public sealed record IngestionResult(Bill Bill, int ChunksIndexed, string? Warning = null);
