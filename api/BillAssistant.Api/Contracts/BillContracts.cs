using BillAssistant.Core.Models;

namespace BillAssistant.Api.Contracts;

/// <summary>A bill as the web app sees it.</summary>
public sealed record BillResponse(
    Guid Id,
    string FileName,
    string Utility,
    string? Provider,
    string? AccountNumberLast4,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    string PeriodLabel,
    decimal? AmountDue,
    string? Currency,
    DateOnly? DueDate,
    double? UsageQuantity,
    string? UsageUnit,
    int PageCount,
    int ChunkCount,
    string MetadataStatus,
    string? MetadataNotes,
    DateTimeOffset UploadedAt)
{
    public static BillResponse Create(Bill bill) => new(
        bill.Id,
        bill.FileName,
        bill.Utility.ToString(),
        bill.ProviderName,
        bill.AccountNumber,
        bill.PeriodStart,
        bill.PeriodEnd,
        bill.PeriodLabel,
        bill.AmountDue,
        bill.Currency,
        bill.DueDate,
        bill.UsageQuantity,
        bill.UsageUnit,
        bill.PageCount,
        bill.ChunkCount,
        bill.MetadataStatus.ToString(),
        bill.MetadataNotes,
        bill.UploadedAt);
}

/// <summary>Result of uploading a bill.</summary>
public sealed record IngestResponse(BillResponse Bill, int ChunksIndexed, string? Warning);

/// <summary>One earlier exchange, sent back so a follow-up can be resolved against it.</summary>
public sealed record ChatTurn(string Question, string Answer);

/// <summary>A question about the bills.</summary>
/// <param name="History">
/// Earlier exchanges in this conversation, oldest first. The server is stateless, so the client
/// carries the conversation; only the most recent few are used.
/// </param>
public sealed record AskRequest(
    string Question,
    string? Utility = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int TopK = 6,
    IReadOnlyList<ChatTurn>? History = null);

/// <summary>An answer, with the passages it was grounded in.</summary>
public sealed record AskResponse(string Answer, IReadOnlyList<CitationResponse> Citations, TotalsResponse? Totals);

public sealed record CitationResponse(Guid BillId, string FileName, int PageNumber, string Snippet, string Utility, double? Score)
{
    public static CitationResponse Create(Citation c) => new(c.BillId, c.FileName, c.PageNumber, c.Snippet, c.Utility, c.Score);
}

public sealed record TotalsResponse(
    int BillCount,
    decimal TotalAmount,
    string Currency,
    double? TotalUsage,
    string? UsageUnit,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<PeriodTotalResponse> Series)
{
    public static TotalsResponse? Create(BillTotals? t) =>
        t is null
            ? null
            : new TotalsResponse(
                t.BillCount, t.TotalAmount, t.Currency, t.TotalUsage, t.UsageUnit, t.From, t.To,
                [.. (t.Series ?? []).Select(PeriodTotalResponse.Create)]);
}

/// <summary>One period's figures, oldest first, so a client can plot or compare them.</summary>
public sealed record PeriodTotalResponse(
    string Label,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal Amount,
    double? Usage,
    string? UsageUnit,
    string Utility)
{
    public static PeriodTotalResponse Create(PeriodTotal p) =>
        new(p.Label, p.PeriodStart, p.PeriodEnd, p.Amount, p.Usage, p.UsageUnit, p.Utility.ToString());
}
