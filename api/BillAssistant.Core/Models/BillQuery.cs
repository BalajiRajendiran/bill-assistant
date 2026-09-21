namespace BillAssistant.Core.Models;

/// <summary>Filter for listing bills and for computing totals.</summary>
/// <param name="Utility">The single kind to narrow to - what listing by query string uses.</param>
/// <param name="Utilities">
/// Several kinds, for a question that named more than one. Takes precedence over
/// <paramref name="Utility"/>; read <see cref="BillQuery.Kinds"/> rather than either field.
/// </param>
public sealed record BillQuery(
    UtilityKind? Utility = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Limit = 200,
    IReadOnlyList<UtilityKind>? Utilities = null)
{
    /// <summary>The kinds this query covers, empty for "every kind".</summary>
    public IReadOnlyList<UtilityKind> Kinds =>
        Utilities is { Count: > 0 } many ? many : Utility is { } one ? [one] : [];
}
