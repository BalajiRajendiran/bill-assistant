using System.Net;
using System.Net.Http.Json;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BillAssistant.Core.Tests.Api;

/// <summary>
/// Contract tests for the HTTP surface. Every service the endpoints touch is faked, so these run
/// without Ollama, Qdrant or a database, and test routing, validation and mapping rather than RAG.
/// </summary>
public sealed class EndpointTests : IClassFixture<EndpointTests.Factory>
{
    private readonly Factory _factory;

    public EndpointTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeIngestion Ingestion { get; } = new();

        public FakeChat Chat { get; } = new();

        public FakeRepository Repository { get; } = new();

        public FakeChunkStore ChunkStore { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                Replace<IBillIngestionService>(services, Ingestion);
                Replace<IBillChatService>(services, Chat);
                Replace<IBillRepository>(services, Repository);
                Replace<IBillChunkStore>(services, ChunkStore);
            });

            return base.CreateHost(builder);
        }

        private static void Replace<T>(IServiceCollection services, T instance)
            where T : class
        {
            foreach (var existing in services.Where(d => d.ServiceType == typeof(T)).ToList())
            {
                services.Remove(existing);
            }

            services.AddSingleton(instance);
        }
    }

    public sealed class FakeIngestion : IBillIngestionService
    {
        public Exception? Throw { get; set; }

        public Task<IngestionResult> IngestAsync(Stream pdfStream, string fileName, CancellationToken ct = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            var bill = new Bill { FileName = fileName, Utility = UtilityKind.Electricity, AmountDue = 84.21m, Currency = "USD", PageCount = 1, ChunkCount = 3, MetadataStatus = MetadataStatus.Extracted };
            return Task.FromResult(new IngestionResult(bill, 3));
        }
    }

    public sealed class FakeChat : IBillChatService
    {
        public ChatRequest? LastRequest { get; private set; }

        public Task<ChatAnswer> AskAsync(ChatRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatAnswer(
                "It was $84.21.",
                [new Citation(Guid.NewGuid(), "electric.pdf", 1, "TOTAL AMOUNT DUE $84.21", "Electricity", 0.91)],
                new BillTotals(1, 84.21m, "USD", 402, "kWh", new DateOnly(2025, 7, 1), new DateOnly(2025, 7, 31))));
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            yield return ChatStreamEvent.Sources([], null);
            yield return ChatStreamEvent.Token("It was ");
            yield return ChatStreamEvent.Token("$84.21.");
            yield return ChatStreamEvent.Done();
            await Task.CompletedTask;
        }
    }

    public sealed class FakeRepository : IBillRepository
    {
        public List<Bill> Bills { get; } = [];

        public BillQuery? LastQuery { get; private set; }

        public Task AddAsync(Bill bill, CancellationToken ct = default)
        {
            Bills.Add(bill);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Bill bill, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Bills.FirstOrDefault(b => b.Id == id));

        public Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<Bill>>(Bills);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Bills.RemoveAll(b => b.Id == id) > 0);

        public Task<BillTotals?> ComputeTotalsAsync(BillQuery query, CancellationToken ct = default) =>
            Task.FromResult<BillTotals?>(null);
    }

    public sealed class FakeChunkStore : IBillChunkStore
    {
        public List<Guid> Deleted { get; } = [];

        public string CollectionName => "fake";

        public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<BillChunk> chunks, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByBillAsync(Guid billId, CancellationToken ct = default)
        {
            Deleted.Add(billId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScoredChunk>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int top, ChunkFilter? filter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScoredChunk>>([]);
    }

    private static MultipartFormDataContent Pdf(string fileName, byte[]? bytes = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? [0x25, 0x50, 0x44, 0x46]);
        content.Add(file, "file", fileName);
        return content;
    }

    [Fact]
    public async Task UploadingAPdf_Returns201WithTheBill()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/bills", Pdf("electric.pdf"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestBody>();
        Assert.Equal("electric.pdf", body!.Bill.FileName);
        Assert.Equal(84.21m, body.Bill.AmountDue);
        Assert.Equal(3, body.ChunksIndexed);
    }

    [Fact]
    public async Task UploadingANonPdf_IsRejected()
    {
        var response = await _factory.CreateClient().PostAsync("/api/bills", Pdf("notes.txt"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PDF", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UploadingWithNoFile_IsRejected()
    {
        var response = await _factory.CreateClient().PostAsync("/api/bills", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnreadablePdf_Returns400WithTheReason()
    {
        _factory.Ingestion.Throw = new UnreadablePdfException("This PDF has little or no text layer.");
        try
        {
            var response = await _factory.CreateClient().PostAsync("/api/bills", Pdf("scan.pdf"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("text layer", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            _factory.Ingestion.Throw = null;
        }
    }

    [Fact]
    public async Task ListingWithAnUnknownUtility_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync("/api/bills?utility=telepathy");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListingPassesTheFilterThrough()
    {
        var response = await _factory.CreateClient().GetAsync("/api/bills?utility=water&from=2025-01-01&to=2025-12-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UtilityKind.Water, _factory.Repository.LastQuery!.Utility);
        Assert.Equal(new DateOnly(2025, 1, 1), _factory.Repository.LastQuery.From);
    }

    [Fact]
    public async Task GettingAMissingBill_Returns404()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/bills/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletingABill_AlsoRemovesItsChunks()
    {
        var bill = new Bill { FileName = "gone.pdf" };
        await _factory.Repository.AddAsync(bill);

        var response = await _factory.CreateClient().DeleteAsync($"/api/bills/{bill.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(bill.Id, _factory.ChunkStore.Deleted);
    }

    [Fact]
    public async Task AskingAQuestion_ReturnsAnswerCitationsAndTotals()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/chat", new { question = "What was my electricity bill?" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AskBody>();

        Assert.Equal("It was $84.21.", body!.Answer);
        Assert.Single(body.Citations);
        Assert.Equal(84.21m, body.Totals!.TotalAmount);
    }

    [Fact]
    public async Task AnEmptyQuestion_IsRejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/chat", new { question = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownUtilityFilter_IsRejected()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/chat", new { question = "what did I pay", utility = "telepathy" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StreamingReturnsServerSentEvents()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/chat/stream", new { question = "What was my electricity bill?" });

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);
        Assert.Contains("\"type\":\"token\"", body);
        Assert.Contains("\"type\":\"done\"", body);
    }

    private sealed record IngestBody(BillBody Bill, int ChunksIndexed, string? Warning);

    private sealed record BillBody(Guid Id, string FileName, decimal? AmountDue);

    private sealed record AskBody(string Answer, List<CitationBody> Citations, TotalsBody? Totals);

    private sealed record CitationBody(string FileName, int PageNumber);

    private sealed record TotalsBody(int BillCount, decimal TotalAmount, string Currency);
}
