using BillAssistant.Core.Retrieval;

namespace BillAssistant.Core.Tests.Retrieval;

public class AnswerTextTests
{
    [Fact]
    public void AnAnswerWithoutScaffoldingIsUntouched()
    {
        Assert.Equal("Your water bill was 206.24 USD. [2]", AnswerText.Clean("Your water bill was 206.24 USD. [2]"));
    }

    [Theory]
    [InlineData("The water bill is $206.24.\n\n<excerpts> [4] </excerpts>")]
    [InlineData("The water bill is $206.24.\n\n<totals>\nBy period (oldest first):\n  2025-05-01: 113.51 USD")]
    [InlineData("The water bill is $206.24.</totals>")]
    public void EchoedScaffoldingIsCutAway(string reply)
    {
        Assert.Equal("The water bill is $206.24.", AnswerText.Clean(reply));
    }

    [Fact]
    public void ALessThanSignThatIsNotATagSurvives()
    {
        Assert.Equal("Usage was < 500 kWh.", AnswerText.Clean("Usage was < 500 kWh."));
    }

    [Fact]
    public void StreamedTokensAreForwardedUnchangedWhenThereIsNoScaffolding()
    {
        var filter = new AnswerText.Filter();
        var output = string.Concat(new[] { "Your ", "water ", "bill ", "was ", "206.24 ", "USD." }.Select(filter.Push))
            + filter.Flush();

        Assert.Equal("Your water bill was 206.24 USD.", output);
    }

    [Fact]
    public void ATagSplitAcrossTokensIsStillCaught()
    {
        var filter = new AnswerText.Filter();

        // "<excerpts>" arrives one fragment at a time, as it does from a real stream.
        var output = string.Concat(new[] { "Done. ", "<", "exc", "erpts", "> [4]" }.Select(filter.Push))
            + filter.Flush();

        Assert.Equal("Done. ", output);
    }

    [Fact]
    public void TextHeldBackButNeverCompletingATag_IsStillEmitted()
    {
        var filter = new AnswerText.Filter();
        var output = string.Concat(new[] { "Usage was ", "<", " 500 kWh." }.Select(filter.Push)) + filter.Flush();

        Assert.Equal("Usage was < 500 kWh.", output);
    }

    [Fact]
    public void NothingIsEmittedAfterATagHasBeenSeen()
    {
        var filter = new AnswerText.Filter();
        filter.Push("Done. <totals>");

        Assert.Equal(string.Empty, filter.Push("more text the reader must not see"));
        Assert.Equal(string.Empty, filter.Flush());
    }
}
