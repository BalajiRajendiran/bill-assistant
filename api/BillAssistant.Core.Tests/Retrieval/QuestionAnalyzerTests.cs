using BillAssistant.Core.Models;
using BillAssistant.Core.Retrieval;

namespace BillAssistant.Core.Tests.Retrieval;

public class QuestionAnalyzerTests
{
    private static readonly DateOnly Today = new(2026, 9, 16);

    [Theory]
    [InlineData("How much did I pay for electricity in July?", true)]
    [InlineData("What did I spend altogether last year?", true)]
    [InlineData("Is my water usage trending up?", true)]
    [InlineData("Is my water usage going up?", true)]
    [InlineData("Has my gas bill gone down?", true)]
    [InlineData("Is electricity more expensive than last year?", true)]
    [InlineData("When is my gas bill due?", false)]
    [InlineData("What is the tier 3 rate?", false)]
    public void AggregateQuestions_AreRecognised(string question, bool expected)
    {
        Assert.Equal(expected, QuestionAnalyzer.Analyse(question, Today).WantsTotals);
    }

    [Theory]
    [InlineData("how much was my electric bill", UtilityKind.Electricity)]
    [InlineData("what did the water and sewer cost", UtilityKind.Water)]
    [InlineData("natural gas usage", UtilityKind.Gas)]
    [InlineData("broadband charges", UtilityKind.Internet)]
    [InlineData("how many kwh did I use", UtilityKind.Electricity)]
    [InlineData("what do I owe", null)]
    public void UtilityIsInferredFromWording(string question, UtilityKind? expected)
    {
        Assert.Equal(expected, QuestionAnalyzer.Analyse(question, Today).Utility);
    }

    [Fact]
    public void BareMonth_ResolvesToMostRecentOccurrence_AndFlagsTheGuess()
    {
        var intent = QuestionAnalyzer.Analyse("what did I pay in July", Today);

        Assert.Equal(new DateOnly(2026, 7, 1), intent.From);
        Assert.Equal(new DateOnly(2026, 7, 31), intent.To);
        Assert.True(intent.YearWasGuessed);
    }

    [Fact]
    public void MonthLaterThanToday_ResolvesToLastYear()
    {
        var intent = QuestionAnalyzer.Analyse("what did I pay in December", Today);

        Assert.Equal(new DateOnly(2025, 12, 1), intent.From);
        Assert.Equal(new DateOnly(2025, 12, 31), intent.To);
    }

    [Fact]
    public void ExplicitYear_IsNotAGuess()
    {
        var intent = QuestionAnalyzer.Analyse("what did I pay in July 2025", Today);

        Assert.Equal(new DateOnly(2025, 7, 1), intent.From);
        Assert.False(intent.YearWasGuessed);
    }

    [Theory]
    [InlineData("last month", "2026-08-01", "2026-08-31")]
    [InlineData("this month", "2026-09-01", "2026-09-30")]
    [InlineData("last quarter", "2026-04-01", "2026-06-30")]
    [InlineData("this quarter", "2026-07-01", "2026-09-30")]
    [InlineData("last year", "2025-01-01", "2025-12-31")]
    [InlineData("in 2025", "2025-01-01", "2025-12-31")]
    [InlineData("the last 6 months", "2026-03-16", "2026-09-16")]
    public void RelativePeriods_Resolve(string phrase, string from, string to)
    {
        var range = QuestionAnalyzer.InferRange($"what did I spend {phrase}", Today);

        Assert.Equal(DateOnly.Parse(from), range.From);
        Assert.Equal(DateOnly.Parse(to), range.To);
    }

    [Fact]
    public void QuestionWithNoPeriod_LeavesTheRangeOpen()
    {
        var intent = QuestionAnalyzer.Analyse("what is the late payment fee", Today);

        Assert.Null(intent.From);
        Assert.Null(intent.To);
    }
}
