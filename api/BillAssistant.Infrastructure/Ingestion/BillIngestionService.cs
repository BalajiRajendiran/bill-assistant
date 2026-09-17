using System.Diagnostics;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Chunking;
using BillAssistant.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace BillAssistant.Infrastructure.Ingestion;

/// <summary>
/// Turns an uploaded PDF into a stored bill plus embedded chunks:
/// extract text -> chunk -> embed -> upsert to the vector store, and in parallel extract metadata to SQL.
/// </summary>
public sealed class BillIngestionService(
    IPdfTextExtractor extractor,
    BillChunker chunker,
    BillMetadataExtractor metadataExtractor,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IBillChunkStore chunkStore,
    IBillRepository repository,
    ILogger<BillIngestionService> logger) : IBillIngestionService
{
    /// <summary>Embedding requests are batched; Ollama handles a modest batch far faster than one by one.</summary>
    private const int EmbeddingBatchSize = 32;

    public async Task<IngestionResult> IngestAsync(Stream pdfStream, string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var stopwatch = Stopwatch.StartNew();

        // Throws UnreadablePdfException for scans and non-PDFs; the endpoint turns that into a 400.
        var document = extractor.Extract(pdfStream);
        var chunks = chunker.Chunk(document);

        if (chunks.Count == 0)
        {
            throw new UnreadablePdfException("No text could be chunked from this PDF.");
        }

        var bill = new Bill
        {
            FileName = fileName,
            PageCount = document.Pages.Count,
            ChunkCount = chunks.Count
        };

        // Metadata extraction and embedding are independent and both slow - run them together.
        var metadataTask = metadataExtractor.ExtractAsync(document, ct);
        var embeddingsTask = EmbedAsync(chunks, ct);

        await Task.WhenAll(metadataTask, embeddingsTask);

        var extraction = await metadataTask;
        var embeddings = await embeddingsTask;

        ApplyMetadata(bill, extraction);

        var billChunks = new List<BillChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            billChunks.Add(new BillChunk
            {
                BillId = bill.Id.ToString(),
                Text = chunks[i].Text,
                PageNumber = chunks[i].PageNumber,
                ChunkIndex = chunks[i].ChunkIndex,
                FileName = fileName,
                Utility = bill.Utility.ToString(),
                ProviderName = bill.ProviderName ?? string.Empty,
                PeriodStartDay = bill.PeriodStart?.DayNumber ?? 0,
                PeriodEndDay = bill.PeriodEnd?.DayNumber ?? 0,
                Embedding = embeddings[i]
            });
        }

        await chunkStore.UpsertAsync(billChunks, ct);
        await repository.AddAsync(bill, ct);

        logger.LogInformation(
            "Ingested {FileName}: {Pages} page(s), {Chunks} chunk(s), metadata {Status}, in {Elapsed}ms.",
            fileName, bill.PageCount, bill.ChunkCount, bill.MetadataStatus, stopwatch.ElapsedMilliseconds);

        var warning = bill.MetadataStatus == MetadataStatus.NeedsReview
            ? "The bill text was indexed and is searchable, but its details could not be read reliably, so it is excluded from totals."
            : null;

        return new IngestionResult(bill, billChunks.Count, warning);
    }

    private static void ApplyMetadata(Bill bill, MetadataExtraction extraction)
    {
        var m = extraction.Metadata;

        bill.Utility = m.Utility;
        bill.ProviderName = m.ProviderName;
        bill.AccountNumber = m.AccountNumberLast4;
        bill.PeriodStart = m.PeriodStart;
        bill.PeriodEnd = m.PeriodEnd;
        bill.AmountDue = m.AmountDue;
        bill.Currency = m.Currency;
        bill.DueDate = m.DueDate;
        bill.UsageQuantity = m.UsageQuantity;
        bill.UsageUnit = m.UsageUnit;
        bill.MetadataStatus = extraction.Status;
        bill.MetadataNotes = extraction.Notes;
    }

    private async Task<List<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<TextChunk> chunks, CancellationToken ct)
    {
        var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);

        foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
        {
            var generated = await embeddingGenerator.GenerateAsync([.. batch.Select(c => c.Text)], cancellationToken: ct);
            vectors.AddRange(generated.Select(e => e.Vector));
        }

        return vectors;
    }
}
