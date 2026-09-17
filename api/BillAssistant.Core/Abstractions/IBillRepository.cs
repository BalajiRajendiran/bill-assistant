using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>Relational storage for bills and their extracted metadata.</summary>
public interface IBillRepository
{
    Task AddAsync(Bill bill, CancellationToken ct = default);

    Task UpdateAsync(Bill bill, CancellationToken ct = default);

    Task<Bill?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Exact figures for the matching bills, computed in SQL. Returns null when nothing matches.
    /// This is what numeric questions are answered from - the model is never asked to add up money.
    /// </summary>
    Task<BillTotals?> ComputeTotalsAsync(BillQuery query, CancellationToken ct = default);
}
