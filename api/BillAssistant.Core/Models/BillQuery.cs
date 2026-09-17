namespace BillAssistant.Core.Models;

/// <summary>Filter for listing bills and for computing totals.</summary>
public sealed record BillQuery(
    UtilityKind? Utility = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Limit = 200);
