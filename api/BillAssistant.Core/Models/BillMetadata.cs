namespace BillAssistant.Core.Models;

/// <summary>Validated metadata extracted from a bill's text.</summary>
public sealed record BillMetadata(
    UtilityKind Utility,
    string? ProviderName,
    string? AccountNumberLast4,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal? AmountDue,
    string? Currency,
    DateOnly? DueDate,
    double? UsageQuantity,
    string? UsageUnit);
