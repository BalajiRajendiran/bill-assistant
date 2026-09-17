using BillAssistant.Core.Models;
using BillAssistant.Core.Tests.Fakes;
using BillAssistant.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillAssistant.Core.Tests.Ingestion;

public class BillMetadataExtractorTests
{
    private static PdfDocumentText Document(string text = "Electricity statement. TOTAL AMOUNT DUE $84.21") =>
        new([new PdfPageText(1, text)]);

    private static BillMetadataExtractor Build(params string[] replies) =>
        new(new FakeChatClient(replies), NullLogger<BillMetadataExtractor>.Instance);

    private const string GoodJson =
        """
        {"utility":"Electricity","providerName":"Cascade Power","accountNumber":"8830-1174",
         "periodStart":"2025-07-01","periodEnd":"2025-07-31","amountDue":84.21,"currency":"USD",
         "dueDate":"2025-08-21","usageQuantity":402,"usageUnit":"kWh"}
        """;

    [Fact]
    public async Task ValidJson_IsExtractedAndValidated()
    {
        var result = await Build(GoodJson).ExtractAsync(Document());

        Assert.Equal(MetadataStatus.Extracted, result.Status);
        Assert.Equal(UtilityKind.Electricity, result.Metadata.Utility);
        Assert.Equal(84.21m, result.Metadata.AmountDue);
        Assert.Equal("1174", result.Metadata.AccountNumberLast4);
        Assert.Null(result.Notes);
    }

    [Fact]
    public async Task UnparseableReply_IsRetriedOnce_ThenFlagged()
    {
        var extractor = Build("that is not json", "still not json");

        var result = await extractor.ExtractAsync(Document());

        Assert.Equal(MetadataStatus.NeedsReview, result.Status);
        Assert.NotNull(result.Notes);
    }

    [Fact]
    public async Task FirstReplyBad_SecondGood_Succeeds()
    {
        var result = await Build("nonsense", GoodJson).ExtractAsync(Document());

        Assert.Equal(MetadataStatus.Extracted, result.Status);
        Assert.Equal(84.21m, result.Metadata.AmountDue);
    }

    [Fact]
    public async Task IncompleteMetadata_IsFlaggedForReview_NotPersistedAsFact()
    {
        // Valid JSON, but no amount and no period: nothing that can be aggregated.
        var result = await Build("""{"utility":"Electricity","providerName":"Cascade Power"}""")
            .ExtractAsync(Document());

        Assert.Equal(MetadataStatus.NeedsReview, result.Status);
        Assert.Null(result.Metadata.AmountDue);
    }

    [Fact]
    public async Task ImplausibleValues_AreRejectedRatherThanStored()
    {
        var result = await Build("""
            {"utility":"Electricity","periodStart":"2025-07-31","periodEnd":"2025-07-01",
             "amountDue":-40,"usageQuantity":100,"usageUnit":"kWh"}
            """).ExtractAsync(Document());

        Assert.Equal(MetadataStatus.NeedsReview, result.Status);
        Assert.Null(result.Metadata.AmountDue);
        Assert.Null(result.Metadata.PeriodStart);
    }

    [Fact]
    public async Task ExtractionRunsAtTemperatureZero()
    {
        var client = new FakeChatClient(GoodJson);
        var extractor = new BillMetadataExtractor(client, NullLogger<BillMetadataExtractor>.Instance);

        await extractor.ExtractAsync(Document());

        Assert.Equal(0f, client.ReceivedOptions[0]?.Temperature);
    }

    [Fact]
    public async Task VeryLongBill_IsTruncatedBeforeBeingSent()
    {
        var client = new FakeChatClient(GoodJson);
        var extractor = new BillMetadataExtractor(client, NullLogger<BillMetadataExtractor>.Instance);

        await extractor.ExtractAsync(Document(new string('x', 20_000)));

        var sent = string.Join(' ', client.ReceivedMessages[0].Select(m => m.Text));
        Assert.True(sent.Length < 10_000, $"prompt was {sent.Length} characters");
    }
}
