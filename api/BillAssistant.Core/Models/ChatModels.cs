namespace BillAssistant.Core.Models;

/// <summary>One completed exchange, used to make a follow-up question stand on its own.</summary>
public sealed record ChatExchange(string Question, string Answer);

/// <summary>A question, optionally scoped to a subset of bills.</summary>
/// <param name="History">
/// Earlier exchanges in this conversation, oldest first. Supplied so a follow-up like "can you sum
/// them?" can be resolved into a standalone question before it is embedded - a pronoun retrieves
/// nothing useful, because there is no passage in any bill that looks like "them".
/// </param>
public sealed record ChatRequest(
    string Question,
    UtilityKind? Utility = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int TopK = 6,
    IReadOnlyList<ChatExchange>? History = null);

/// <summary>A grounded answer plus the passages it came from.</summary>
public sealed record ChatAnswer(
    string Answer,
    IReadOnlyList<Citation> Citations,
    BillTotals? Totals = null);

/// <summary>One bill's figures, as a point in a series.</summary>
/// <param name="Utility">
/// Which bill this point came from. Carried so a series spanning several utilities can be recognised
/// as one: comparing the oldest electricity bill to the newest water bill is not a trend.
/// </param>
public sealed record PeriodTotal(
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal Amount,
    double? Usage,
    string? UsageUnit,
    UtilityKind Utility = UtilityKind.Unknown)
{
    public string Label => (PeriodStart, PeriodEnd) switch
    {
        ({ } s, { } e) => $"{s:yyyy-MM-dd} to {e:yyyy-MM-dd}",
        (null, { } e) => $"to {e:yyyy-MM-dd}",
        ({ } s, null) => $"from {s:yyyy-MM-dd}",
        _ => "unknown period"
    };
}

/// <summary>
/// Exact figures computed with SQL over the bills table, attached when the question looks numeric.
/// </summary>
/// <param name="Series">
/// The matched bills in chronological order. A sum alone cannot answer "is my usage going up?" - that
/// needs the individual periods to compare - so the series travels with the total and is rendered into
/// the prompt beside it.
/// </param>
public sealed record BillTotals(
    int BillCount,
    decimal TotalAmount,
    string Currency,
    double? TotalUsage,
    string? UsageUnit,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<PeriodTotal>? Series = null);

/// <summary>One frame of a streamed answer.</summary>
public sealed record ChatStreamEvent(string Type, string? Text = null, IReadOnlyList<Citation>? Citations = null, BillTotals? Totals = null)
{
    public static ChatStreamEvent Token(string text) => new("token", text);
    public static ChatStreamEvent Sources(IReadOnlyList<Citation> citations, BillTotals? totals) => new("sources", null, citations, totals);
    public static ChatStreamEvent Done() => new("done");
    public static ChatStreamEvent Error(string message) => new("error", message);
}
