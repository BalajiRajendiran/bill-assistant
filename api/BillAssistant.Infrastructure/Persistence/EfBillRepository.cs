using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace BillAssistant.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IBillRepository"/> over SQLite.</summary>
public sealed class EfBillRepository(BillDbContext db) : IBillRepository
{
    public async Task AddAsync(Bill bill, CancellationToken ct = default)
    {
        db.Bills.Add(bill);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Bill bill, CancellationToken ct = default)
    {
        db.Bills.Update(bill);
        await db.SaveChangesAsync(ct);
    }

    public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken ct = default)
    {
        var bills = await Filter(db.Bills.AsNoTracking(), query)
            .OrderByDescending(b => b.PeriodEnd ?? DateOnly.MinValue)
            .ThenByDescending(b => b.UploadedAt)
            .Take(query.Limit)
            .ToListAsync(ct);

        return bills;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var deleted = await db.Bills.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    public async Task<BillTotals?> ComputeTotalsAsync(BillQuery query, CancellationToken ct = default)
    {
        // Only bills whose metadata passed validation can be aggregated; a bill we failed to read
        // correctly must not silently drag a total down.
        var matching = Filter(db.Bills.AsNoTracking(), query)
            .Where(b => b.MetadataStatus == MetadataStatus.Extracted && b.AmountDueMinor != null);

        // Mixing currencies in one sum would be meaningless, so aggregate the dominant one and
        // report only that. In a single household this is virtually always a single currency.
        var currency = await matching
            .GroupBy(b => b.Currency)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefaultAsync(ct);

        if (currency is null)
        {
            return null;
        }

        var scoped = matching.Where(b => b.Currency == currency);

        var summary = await scoped
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                TotalMinor = g.Sum(b => b.AmountDueMinor!.Value),
                From = g.Min(b => b.PeriodStart),
                To = g.Max(b => b.PeriodEnd)
            })
            .FirstOrDefaultAsync(ct);

        if (summary is null || summary.Count == 0)
        {
            return null;
        }

        // Usage only adds up when every bill measures the same thing; kWh plus CCF is not a number.
        var units = await scoped.Where(b => b.UsageUnit != null && b.UsageQuantity != null)
            .Select(b => b.UsageUnit!)
            .Distinct()
            .ToListAsync(ct);

        double? totalUsage = null;
        string? usageUnit = null;

        if (units.Count == 1)
        {
            usageUnit = units[0];
            totalUsage = await scoped.Where(b => b.UsageUnit == usageUnit && b.UsageQuantity != null)
                .SumAsync(b => b.UsageQuantity!.Value, ct);
        }

        // The per-period series. A total alone cannot answer "is my usage going up?"; that needs the
        // individual bills to compare. Capped so a long history cannot bloat the prompt - for a
        // direction of travel the recent periods are the ones that matter.
        const int maxSeriesPoints = 24;

        var rows = await scoped
            .OrderByDescending(b => b.PeriodEnd ?? DateOnly.MinValue)
            .Take(maxSeriesPoints)
            .Select(b => new
            {
                b.PeriodStart,
                b.PeriodEnd,
                Minor = b.AmountDueMinor!.Value,
                b.UsageQuantity,
                b.UsageUnit
            })
            .ToListAsync(ct);

        var series = rows
            .OrderBy(r => r.PeriodEnd ?? DateOnly.MinValue)
            .Select(r => new PeriodTotal(r.PeriodStart, r.PeriodEnd, r.Minor / 100m, r.UsageQuantity, r.UsageUnit))
            .ToList();

        return new BillTotals(
            summary.Count,
            summary.TotalMinor / 100m,
            currency,
            totalUsage,
            usageUnit,
            summary.From,
            summary.To,
            series);
    }

    /// <summary>
    /// A bill matches a date window when its service period overlaps it, not when it sits wholly
    /// inside - a quarterly water bill should still count towards "this summer".
    /// </summary>
    private static IQueryable<Bill> Filter(IQueryable<Bill> source, BillQuery query)
    {
        if (query.Utility is { } utility)
        {
            source = source.Where(b => b.Utility == utility);
        }

        if (query.From is { } from)
        {
            source = source.Where(b => b.PeriodEnd == null || b.PeriodEnd >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(b => b.PeriodStart == null || b.PeriodStart <= to);
        }

        return source;
    }
}
