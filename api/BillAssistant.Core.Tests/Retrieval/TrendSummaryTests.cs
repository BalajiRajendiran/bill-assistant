using BillAssistant.Core.Models;
using BillAssistant.Core.Retrieval;

namespace BillAssistant.Core.Tests.Retrieval;

public class TrendSummaryTests
{
    private static PeriodTotal Point(int month, decimal amount, double? usage, string? unit = "CCF") =>
        new(new DateOnly(2025, month, 1), new DateOnly(2025, month, 28), amount, usage, unit);

    [Fact]
    public void ARisingSeriesIsDescribedWithBothEnds()
    {
        var summary = TrendSummary.Describe([Point(4, 159.53m, 14), Point(7, 206.24m, 19)], "USD");

        Assert.NotNull(summary);
        Assert.Contains("usage rose 36% from 14 to 19 CCF", summary);
        Assert.Contains("amount rose 29% from 159.53 to 206.24 USD", summary);
        Assert.Contains("2025-04-01", summary);
    }

    [Fact]
    public void AFallingSeriesSaysFell()
    {
        var summary = TrendSummary.Describe([Point(4, 200m, 20), Point(7, 100m, 10)], "USD");

        Assert.Contains("amount fell 50%", summary);
        Assert.Contains("usage fell 50%", summary);
    }

    [Fact]
    public void ATinyChangeReadsAsSteady()
    {
        var summary = TrendSummary.Describe([Point(4, 100m, 100), Point(7, 100.5m, 100.5)], "USD");

        Assert.Contains("held steady", summary);
        Assert.DoesNotContain("rose", summary);
    }

    [Fact]
    public void OnlyTheFirstAndLastPeriodsAreCompared()
    {
        // The middle period dips, but the question is where it started and where it ended.
        var summary = TrendSummary.Describe([Point(4, 100m, 10), Point(5, 40m, 4), Point(7, 200m, 20)], "USD");

        Assert.Contains("rose 100%", summary);
        Assert.Contains("from 10 to 20 CCF", summary);
    }

    [Fact]
    public void MismatchedUnitsAreNotCompared()
    {
        var summary = TrendSummary.Describe(
            [Point(4, 100m, 10, "kWh"), Point(7, 200m, 20, "CCF")], "USD");

        Assert.Contains("amount rose", summary);
        Assert.DoesNotContain("usage", summary);
    }

    [Fact]
    public void AMissingUsageLeavesTheAmountComparisonIntact()
    {
        var summary = TrendSummary.Describe([Point(4, 100m, null), Point(7, 150m, 20)], "USD");

        Assert.Contains("amount rose 50%", summary);
        Assert.DoesNotContain("usage", summary);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NothingToCompareYieldsNothing(int points)
    {
        var series = Enumerable.Range(4, points).Select(m => Point(m, 100m, 10)).ToList();

        Assert.Null(TrendSummary.Describe(series, "USD"));
        Assert.Null(TrendSummary.Describe(null, "USD"));
    }

    [Fact]
    public void ASeriesSpanningTwoUtilitiesHasNoDirection()
    {
        // "Add the water and electricity bills" matches both kinds; first-to-last across them would
        // compare an electricity bill with a water bill and report a confident, meaningless percentage.
        string? described = TrendSummary.Describe(
        [
            new PeriodTotal(new(2025, 5, 1), new(2025, 5, 31), 113.51m, 402, "kWh", UtilityKind.Electricity),
            new PeriodTotal(new(2025, 7, 1), new(2025, 9, 30), 206.24m, 19, "CCF", UtilityKind.Water),
        ], "USD");

        Assert.Null(described);
    }

    [Fact]
    public void ASeriesOfOneUtilityStillDescribesItsDirection()
    {
        var described = TrendSummary.Describe(
        [
            new PeriodTotal(new(2025, 4, 1), new(2025, 6, 30), 159.53m, 14, "CCF", UtilityKind.Water),
            new PeriodTotal(new(2025, 7, 1), new(2025, 9, 30), 206.24m, 19, "CCF", UtilityKind.Water),
        ], "USD");

        Assert.Contains("usage rose 36% from 14 to 19 CCF", described);
    }
}
