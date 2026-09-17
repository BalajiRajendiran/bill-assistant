using BillAssistant.Core.Models;
using BillAssistant.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BillAssistant.Core.Tests.Persistence;

/// <summary>
/// Runs against a real SQLite database (in memory) rather than the EF in-memory provider, because the
/// point of these tests is that the aggregates actually translate to SQL - which is exactly what the
/// in-memory provider would not tell us.
/// </summary>
public sealed class EfBillRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BillDbContext _db;
    private readonly EfBillRepository _repository;

    public EfBillRepositoryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new BillDbContext(new DbContextOptionsBuilder<BillDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _repository = new EfBillRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static Bill Bill(
        UtilityKind utility,
        decimal? amount,
        DateOnly start,
        DateOnly end,
        double? usage = null,
        string? unit = null,
        MetadataStatus status = MetadataStatus.Extracted,
        string currency = "USD") => new()
        {
            FileName = $"{utility}-{start:yyyy-MM}.pdf",
            Utility = utility,
            AmountDue = amount,
            Currency = currency,
            PeriodStart = start,
            PeriodEnd = end,
            UsageQuantity = usage,
            UsageUnit = unit,
            MetadataStatus = status,
        };

    private async Task SeedAsync()
    {
        await _repository.AddAsync(Bill(UtilityKind.Electricity, 113.51m, new(2025, 5, 1), new(2025, 5, 31), 402, "kWh"));
        await _repository.AddAsync(Bill(UtilityKind.Electricity, 147.63m, new(2025, 6, 1), new(2025, 6, 30), 518, "kWh"));
        await _repository.AddAsync(Bill(UtilityKind.Electricity, 220.57m, new(2025, 7, 1), new(2025, 7, 31), 734, "kWh"));
        await _repository.AddAsync(Bill(UtilityKind.Water, 159.53m, new(2025, 4, 1), new(2025, 6, 30), 14, "CCF"));
        await _repository.AddAsync(Bill(UtilityKind.Gas, 30.59m, new(2025, 7, 1), new(2025, 7, 31), 18, "therms"));
    }

    [Fact]
    public async Task MoneySurvivesARoundTripExactly()
    {
        await _repository.AddAsync(Bill(UtilityKind.Gas, 30.59m, new(2025, 7, 1), new(2025, 7, 31)));

        var stored = (await _repository.ListAsync(new BillQuery()))[0];

        Assert.Equal(30.59m, stored.AmountDue);
        Assert.Equal(3059, stored.AmountDueMinor);
    }

    [Fact]
    public async Task ListIsOrderedByMostRecentPeriod()
    {
        await SeedAsync();

        var bills = await _repository.ListAsync(new BillQuery());

        Assert.Equal(5, bills.Count);
        Assert.Equal(new DateOnly(2025, 7, 31), bills[0].PeriodEnd);
    }

    [Fact]
    public async Task TotalsSumExactly_InSql()
    {
        await SeedAsync();

        var totals = await _repository.ComputeTotalsAsync(new BillQuery(UtilityKind.Electricity));

        Assert.NotNull(totals);
        Assert.Equal(3, totals!.BillCount);
        Assert.Equal(481.71m, totals.TotalAmount);
        Assert.Equal(1654, totals.TotalUsage);
        Assert.Equal("kWh", totals.UsageUnit);
    }

    [Fact]
    public async Task TotalsAcrossEverything_AddUp()
    {
        await SeedAsync();

        var totals = await _repository.ComputeTotalsAsync(new BillQuery());

        Assert.Equal(5, totals!.BillCount);
        Assert.Equal(671.83m, totals.TotalAmount);
    }

    [Fact]
    public async Task TotalsCarryAPerPeriodSeries_OldestFirst()
    {
        await SeedAsync();

        var totals = await _repository.ComputeTotalsAsync(new BillQuery(UtilityKind.Electricity));

        var series = totals!.Series;
        Assert.NotNull(series);
        Assert.Equal(3, series!.Count);

        // Chronological, so reading top to bottom is reading the trend.
        Assert.Equal([new DateOnly(2025, 5, 31), new DateOnly(2025, 6, 30), new DateOnly(2025, 7, 31)],
            series.Select(p => p.PeriodEnd));
        Assert.Equal([113.51m, 147.63m, 220.57m], series.Select(p => p.Amount));
        Assert.Equal([402d, 518d, 734d], series.Select(p => p.Usage));
        Assert.All(series, p => Assert.Equal("kWh", p.UsageUnit));
    }

    [Fact]
    public async Task SeriesKeepsEachPeriodSeparate_EvenWhenUnitsDiffer()
    {
        await SeedAsync();

        var totals = await _repository.ComputeTotalsAsync(new BillQuery());

        // The summed usage is withheld across mixed units, but each period still carries its own.
        Assert.Null(totals!.TotalUsage);
        Assert.Equal(5, totals.Series!.Count);
        Assert.Contains(totals.Series, p => p.UsageUnit == "CCF");
        Assert.Contains(totals.Series, p => p.UsageUnit == "kWh");
    }

    [Fact]
    public async Task MixedUsageUnits_AreNotAddedTogether()
    {
        await SeedAsync();

        var totals = await _repository.ComputeTotalsAsync(new BillQuery());

        // kWh, CCF and therms cannot be summed into a single figure.
        Assert.Null(totals!.TotalUsage);
        Assert.Null(totals.UsageUnit);
    }

    [Fact]
    public async Task BillsNeedingReview_AreExcludedFromTotals()
    {
        await _repository.AddAsync(Bill(UtilityKind.Electricity, 100m, new(2025, 5, 1), new(2025, 5, 31)));
        await _repository.AddAsync(Bill(UtilityKind.Electricity, 999m, new(2025, 6, 1), new(2025, 6, 30), status: MetadataStatus.NeedsReview));

        var totals = await _repository.ComputeTotalsAsync(new BillQuery());

        Assert.Equal(1, totals!.BillCount);
        Assert.Equal(100m, totals.TotalAmount);
    }

    [Fact]
    public async Task DateFilterMatchesOverlappingPeriods_NotJustContainedOnes()
    {
        await SeedAsync();

        // A quarterly water bill covering April-June overlaps a query for June alone.
        var june = await _repository.ListAsync(new BillQuery(UtilityKind.Water, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30)));

        Assert.Single(june);
    }

    [Fact]
    public async Task NoMatchingBills_YieldsNoTotals()
    {
        await SeedAsync();

        Assert.Null(await _repository.ComputeTotalsAsync(new BillQuery(UtilityKind.Internet)));
    }

    [Fact]
    public async Task DeleteRemovesTheBill()
    {
        await SeedAsync();
        var bill = (await _repository.ListAsync(new BillQuery()))[0];

        Assert.True(await _repository.DeleteAsync(bill.Id));
        Assert.False(await _repository.DeleteAsync(bill.Id));
        Assert.Null(await _repository.GetAsync(bill.Id));
    }
}
