using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Chunking;
using BillAssistant.Core.Models;
using BillAssistant.Core.Tests.Fakes;
using BillAssistant.Infrastructure.Ingestion;
using BillAssistant.Infrastructure.Vectors;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillAssistant.Core.Tests.Ingestion;

/// <summary>
/// Exercises the whole ingest path against an in-memory vector store and fake models, so it runs in
/// milliseconds with neither Ollama nor Qdrant present.
/// </summary>
public class BillIngestionServiceTests
{
    private const string GoodJson =
        """
        {"utility":"Water","providerName":"Springfield Water","accountNumber":"4471-2099",
         "periodStart":"2025-04-01","periodEnd":"2025-06-30","amountDue":159.53,"currency":"USD",
         "dueDate":"2025-07-25","usageQuantity":14,"usageUnit":"CCF"}
        """;

    private sealed class StubExtractor(PdfDocumentText document) : IPdfTextExtractor
    {
        public PdfDocumentText Extract(Stream pdfStream) => document;
    }

    private sealed class RecordingRepository : IBillRepository
    {
        public List<Bill> Added { get; } = [];

        public Task AddAsync(Bill bill, CancellationToken ct = default)
        {
            Added.Add(bill);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Bill bill, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Added.FirstOrDefault(b => b.Id == id));

        public Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bill>>(Added);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<BillTotals?> ComputeTotalsAsync(BillQuery query, CancellationToken ct = default) =>
            Task.FromResult<BillTotals?>(null);
    }

    private static (BillIngestionService Service, RecordingRepository Repository, IBillChunkStore Store) Build(
        PdfDocumentText document,
        params string[] replies)
    {
        var embeddings = new FakeEmbeddingGenerator();
        var store = new VectorDataBillChunkStore(
            new InMemoryVectorStore(),
            "test_chunks",
            embeddings.Dimensions,
            NullLogger<VectorDataBillChunkStore>.Instance);

        var repository = new RecordingRepository();

        var service = new BillIngestionService(
            new StubExtractor(document),
            new BillChunker(),
            new BillMetadataExtractor(new FakeChatClient(replies), NullLogger<BillMetadataExtractor>.Instance),
            embeddings,
            store,
            repository,
            NullLogger<BillIngestionService>.Instance);

        return (service, repository, store);
    }

    private static PdfDocumentText TwoPages() => new(
    [
        new PdfPageText(1, string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"Paragraph {i} of the bill, with charges and credits listed."))),
        new PdfPageText(2, "Terms and conditions apply. Late payment charge of 1.5% per month."),
    ]);

    [Fact]
    public async Task IngestingABill_StoresItWithChunksAndMetadata()
    {
        var (service, repository, store) = Build(TwoPages(), GoodJson);

        var result = await service.IngestAsync(new MemoryStream([1, 2, 3]), "water-2025-q2.pdf");

        Assert.Equal("water-2025-q2.pdf", result.Bill.FileName);
        Assert.Equal(2, result.Bill.PageCount);
        Assert.True(result.ChunksIndexed > 0);
        Assert.Equal(result.ChunksIndexed, result.Bill.ChunkCount);
        Assert.Single(repository.Added);
        Assert.Null(result.Warning);

        Assert.Equal(UtilityKind.Water, result.Bill.Utility);
        Assert.Equal(159.53m, result.Bill.AmountDue);
        Assert.Equal(15953, result.Bill.AmountDueMinor);
        Assert.Equal(new DateOnly(2025, 6, 30), result.Bill.PeriodEnd);

        var found = await store.SearchAsync(FakeEmbeddingGenerator.Vector("Paragraph 1", 8), 5);
        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task ChunksCarryTheirBillsMetadataForFiltering()
    {
        var (service, _, store) = Build(TwoPages(), GoodJson);

        var result = await service.IngestAsync(new MemoryStream([1]), "water.pdf");

        var chunks = await store.SearchAsync(FakeEmbeddingGenerator.Vector("charges", 8), 20);
        Assert.All(chunks, c =>
        {
            Assert.Equal(result.Bill.Id.ToString(), c.Chunk.BillId);
            Assert.Equal("Water", c.Chunk.Utility);
            Assert.Equal(new DateOnly(2025, 4, 1).DayNumber, c.Chunk.PeriodStartDay);
            Assert.Equal("water.pdf", c.Chunk.FileName);
        });
    }

    [Fact]
    public async Task UnreadableMetadata_StillIndexesTheText_AndWarns()
    {
        var (service, _, store) = Build(TwoPages(), "not json", "still not json");

        var result = await service.IngestAsync(new MemoryStream([1]), "mystery.pdf");

        Assert.Equal(MetadataStatus.NeedsReview, result.Bill.MetadataStatus);
        Assert.NotNull(result.Warning);
        Assert.True(result.ChunksIndexed > 0);

        // Searchable even though it cannot be counted.
        var chunks = await store.SearchAsync(FakeEmbeddingGenerator.Vector("charges", 8), 20);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal("Unknown", c.Chunk.Utility));
    }

    [Fact]
    public async Task EmbeddingsAreRequestedInBatches_NotOnePerChunk()
    {
        var embeddings = new FakeEmbeddingGenerator();
        var store = new VectorDataBillChunkStore(new InMemoryVectorStore(), "batched", embeddings.Dimensions, NullLogger<VectorDataBillChunkStore>.Instance);

        var service = new BillIngestionService(
            new StubExtractor(TwoPages()),
            new BillChunker(),
            new BillMetadataExtractor(new FakeChatClient(GoodJson), NullLogger<BillMetadataExtractor>.Instance),
            embeddings,
            store,
            new RecordingRepository(),
            NullLogger<BillIngestionService>.Instance);

        var result = await service.IngestAsync(new MemoryStream([1]), "b.pdf");

        Assert.True(result.ChunksIndexed > 1);
        Assert.Equal(1, embeddings.BatchCount);
    }

    [Fact]
    public async Task DeletingABill_RemovesOnlyItsChunks()
    {
        var (service, _, store) = Build(TwoPages(), GoodJson);

        var first = await service.IngestAsync(new MemoryStream([1]), "first.pdf");
        var second = await service.IngestAsync(new MemoryStream([2]), "second.pdf");

        await store.DeleteByBillAsync(first.Bill.Id);

        var remaining = await store.SearchAsync(FakeEmbeddingGenerator.Vector("charges", 8), 50);
        Assert.NotEmpty(remaining);
        Assert.All(remaining, c => Assert.Equal(second.Bill.Id.ToString(), c.Chunk.BillId));
    }

    [Fact]
    public async Task APdfWithNoUsableText_IsRejected()
    {
        var (service, _, _) = Build(new PdfDocumentText([new PdfPageText(1, "   ")]), GoodJson);

        await Assert.ThrowsAsync<UnreadablePdfException>(
            () => service.IngestAsync(new MemoryStream([1]), "scan.pdf"));
    }
}
