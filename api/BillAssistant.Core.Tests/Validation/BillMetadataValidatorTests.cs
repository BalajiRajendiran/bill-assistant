using BillAssistant.Core.Models;
using BillAssistant.Core.Validation;

namespace BillAssistant.Core.Tests.Validation;

public class BillMetadataValidatorTests
{
    private static BillMetadataDraft Good() => new()
    {
        Utility = "Electricity",
        ProviderName = "Pacific Grid Energy",
        AccountNumber = "8830-1174-2299",
        PeriodStart = "2025-07-01",
        PeriodEnd = "2025-07-31",
        AmountDue = 128.45,
        Currency = "USD",
        DueDate = "2025-08-18",
        UsageQuantity = 512.0,
        UsageUnit = "kWh"
    };

    [Fact]
    public void WellFormedDraft_PassesCleanly()
    {
        var result = BillMetadataValidator.Validate(Good(), out var problems);

        Assert.Empty(problems);
        Assert.Equal(UtilityKind.Electricity, result.Utility);
        Assert.Equal("Pacific Grid Energy", result.ProviderName);
        Assert.Equal(new DateOnly(2025, 7, 1), result.PeriodStart);
        Assert.Equal(128.45m, result.AmountDue);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(512.0, result.UsageQuantity);
        Assert.True(BillMetadataValidator.IsUsable(result));
    }

    [Fact]
    public void AccountNumber_IsReducedToLastFourCharacters()
    {
        var result = BillMetadataValidator.Validate(Good(), out _);
        Assert.Equal("2299", result.AccountNumberLast4);
    }

    [Theory]
    [InlineData("2025-13-45")]
    [InlineData("not a date")]
    [InlineData("1823-01-01")]
    public void ImplausibleDates_AreDroppedAndReported(string date)
    {
        var draft = Good();
        draft.PeriodStart = date;

        var result = BillMetadataValidator.Validate(draft, out var problems);

        Assert.Null(result.PeriodStart);
        Assert.Contains(problems, p => p.Contains("PeriodStart"));
    }

    [Fact]
    public void ReversedServicePeriod_DiscardsBothEnds()
    {
        var draft = Good();
        draft.PeriodStart = "2025-07-31";
        draft.PeriodEnd = "2025-07-01";

        var result = BillMetadataValidator.Validate(draft, out var problems);

        Assert.Null(result.PeriodStart);
        Assert.Null(result.PeriodEnd);
        Assert.Contains(problems, p => p.Contains("before it starts"));
        Assert.False(BillMetadataValidator.IsUsable(result));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(9_999_999.0)]
    public void ImplausibleAmounts_AreDropped(double amount)
    {
        var draft = Good();
        draft.AmountDue = amount;

        var result = BillMetadataValidator.Validate(draft, out var problems);

        Assert.Null(result.AmountDue);
        Assert.Contains(problems, p => p.Contains("amount due", StringComparison.OrdinalIgnoreCase));
        Assert.False(BillMetadataValidator.IsUsable(result));
    }

    [Theory]
    [InlineData("Electric utility", UtilityKind.Electricity)]
    [InlineData("POWER", UtilityKind.Electricity)]
    [InlineData("Water & Sewer", UtilityKind.Water)]
    [InlineData("Natural Gas", UtilityKind.Gas)]
    [InlineData("Broadband", UtilityKind.Internet)]
    [InlineData("Trash collection", UtilityKind.Waste)]
    [InlineData("", UtilityKind.Unknown)]
    public void UtilitySynonyms_AreRecognised(string input, UtilityKind expected)
    {
        Assert.Equal(expected, BillMetadataValidator.ParseUtility(input));
    }

    [Theory]
    [InlineData("$", "USD")]
    [InlineData("usd", "USD")]
    [InlineData("gbp", "GBP")]
    [InlineData(null, "USD")] // an amount with no stated currency defaults to USD
    public void Currency_IsNormalised(string? input, string expected)
    {
        var draft = Good();
        draft.Currency = input;

        var result = BillMetadataValidator.Validate(draft, out _);

        Assert.Equal(expected, result.Currency);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("N/A")]
    [InlineData("  ")]
    public void PlaceholderStrings_BecomeNull(string value)
    {
        var draft = Good();
        draft.ProviderName = value;
        draft.UsageUnit = value;

        var result = BillMetadataValidator.Validate(draft, out _);

        Assert.Null(result.ProviderName);
        Assert.Null(result.UsageUnit);
    }

    [Fact]
    public void MissingAmount_MakesMetadataUnusableForTotals()
    {
        var draft = Good();
        draft.AmountDue = null;

        var result = BillMetadataValidator.Validate(draft, out var problems);

        Assert.Empty(problems); // absent is not an error, just not aggregatable
        Assert.False(BillMetadataValidator.IsUsable(result));
    }
}
