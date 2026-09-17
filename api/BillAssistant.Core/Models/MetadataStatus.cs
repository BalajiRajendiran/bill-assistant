namespace BillAssistant.Core.Models;

/// <summary>
/// Outcome of the structured-metadata extraction step. A bill whose text was indexed successfully but
/// whose metadata failed validation is still searchable; it is just excluded from numeric aggregates.
/// </summary>
public enum MetadataStatus
{
    Pending = 0,
    Extracted,
    NeedsReview
}
