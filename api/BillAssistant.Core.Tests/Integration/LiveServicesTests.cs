using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Chunking;
using BillAssistant.Core.Models;
using BillAssistant.Infrastructure.Ingestion;
using BillAssistant.Infrastructure.Pdf;
using BillAssistant.Infrastructure.Vectors;
using CommunityToolkit.VectorData.Qdrant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using Qdrant.Client;

namespace BillAssistant.Core.Tests.Integration;

/// <summary>
/// End-to-end checks against the real Ollama and Qdrant this project runs on. These are the tests
/// that would have caught the things unit tests cannot: a model that is not pulled, an embedding
/// dimension that does not match the collection, a payload type the vector store rejects.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LiveServicesTests
{
    private const string EmbeddingModel = "nomic-embed-text";
    private const int Dimensions = 768;

    private static IEmbeddingGenerator<string, Embedding<float>> Embeddings() =>
        new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(5) }, EmbeddingModel);

    private static VectorDataBillChunkStore Store(string collection) =>
        new(new QdrantVectorStore(new QdrantClient("localhost", 6334), ownsClient: true),
            collection,
            Dimensions,
            NullLogger<VectorDataBillChunkStore>.Instance);

    private static string SamplePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples", name);
    }

    [IntegrationFact]
    public async Task TheEmbeddingModelReturnsTheDimensionsTheCollectionExpects()
    {
        var vector = await Embeddings().GenerateVectorAsync("a household electricity bill");

        Assert.Equal(Dimensions, vector.Length);
    }

    [IntegrationFact]
    public async Task ARealPdfSurvivesExtractionAndChunking()
    {
        var extractor = new PdfPigTextExtractor(NullLogger<PdfPigTextExtractor>.Instance);

        await using var file = File.OpenRead(SamplePath("electric-2025-07.pdf"));
        var document = extractor.Extract(file);
        var chunks = new BillChunker().Chunk(document);

        Assert.Contains("TOTAL AMOUNT DUE", document.Pages[0].Text);

        // The charge table must survive as one passage, header included.
        var table = Assert.Single(chunks, c => c.Text.Contains("Energy charge tier 1"));
        Assert.Contains("Energy charge tier 3", table.Text);
        Assert.Contains("Description", table.Text);
    }

    [IntegrationFact]
    public async Task ChunksRoundTripThroughQdrant()
    {
        var collection = $"bill_chunks_test_{Guid.NewGuid():N}";
        var store = Store(collection);
        var embeddings = Embeddings();

        try
        {
            var billId = Guid.NewGuid();
            var texts = new[]
            {
                "TOTAL AMOUNT DUE $220.57 for July electricity.",
                "Late payment charge of 1.5% per month applies after the due date.",
            };

            var vectors = await embeddings.GenerateAsync(texts);

            await store.UpsertAsync(
            [
                .. texts.Select((t, i) => new BillChunk
                {
                    BillId = billId.ToString(),
                    Text = t,
                    PageNumber = 1,
                    ChunkIndex = i,
                    FileName = "electric-2025-07.pdf",
                    Utility = nameof(UtilityKind.Electricity),
                    PeriodStartDay = new DateOnly(2025, 7, 1).DayNumber,
                    PeriodEndDay = new DateOnly(2025, 7, 31).DayNumber,
                    Embedding = vectors[i].Vector,
                })
            ]);

            var query = await embeddings.GenerateVectorAsync("how much was my electricity bill");
            var hits = await store.SearchAsync(query, 2);

            Assert.NotEmpty(hits);
            Assert.Contains(hits, h => h.Chunk.Text.Contains("220.57"));

            // The metadata pre-filter must narrow, not empty, the result.
            var filtered = await store.SearchAsync(query, 2, new ChunkFilter(UtilityKind.Electricity, new DateOnly(2025, 7, 1), new DateOnly(2025, 7, 31)));
            Assert.NotEmpty(filtered);

            var excluded = await store.SearchAsync(query, 2, new ChunkFilter(UtilityKind.Water));
            Assert.Empty(excluded);

            await store.DeleteByBillAsync(billId);
            Assert.Empty(await store.SearchAsync(query, 2));
        }
        finally
        {
            await new QdrantClient("localhost", 6334).DeleteCollectionAsync(collection);
        }
    }

    [IntegrationFact]
    public async Task TheModelExtractsTheKeyFactsFromEverySampleBill()
    {
        var chat = new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(5) }, "llama3.2");
        var extractor = new BillMetadataExtractor(chat, NullLogger<BillMetadataExtractor>.Instance);
        var pdf = new PdfPigTextExtractor(NullLogger<PdfPigTextExtractor>.Instance);

        foreach (var (file, expectedAmount) in new[]
        {
            ("electric-2025-07.pdf", 220.57m),
            ("gas-2025-07.pdf", 30.59m),
            ("water-2025-q2.pdf", 159.53m),
        })
        {
            await using var stream = File.OpenRead(SamplePath(file));
            var result = await extractor.ExtractAsync(pdf.Extract(stream));

            Assert.Equal(MetadataStatus.Extracted, result.Status);
            Assert.Equal(expectedAmount, result.Metadata.AmountDue);
            Assert.NotNull(result.Metadata.PeriodEnd);
        }
    }
}
