using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Tests.Fakes;
using BillAssistant.Infrastructure.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BillAssistant.Core.Tests.Retrieval;

public class BillChatServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubChunkStore : IBillChunkStore
    {
        private readonly List<BillChunk> _chunks;

        public StubChunkStore(params BillChunk[] chunks) => _chunks = [.. chunks];

        public List<ChunkFilter?> ReceivedFilters { get; } = [];

        public string CollectionName => "stub";

        public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<BillChunk> chunks, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByBillAsync(Guid billId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ScoredChunk>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            int top,
            ChunkFilter? filter = null,
            CancellationToken ct = default)
        {
            ReceivedFilters.Add(filter);

            var matches = _chunks.Where(c => Matches(c, filter)).Take(top).Select(c => new ScoredChunk(c, 0.9));
            return Task.FromResult<IReadOnlyList<ScoredChunk>>([.. matches]);
        }

        private static bool Matches(BillChunk chunk, ChunkFilter? filter)
        {
            if (filter is null)
            {
                return true;
            }

            if (filter.Utility is { } utility && chunk.Utility != utility.ToString())
            {
                return false;
            }

            if (filter.From is { } from && chunk.PeriodEndDay != 0 && chunk.PeriodEndDay < from.DayNumber)
            {
                return false;
            }

            return filter.To is not { } to || chunk.PeriodStartDay == 0 || chunk.PeriodStartDay <= to.DayNumber;
        }
    }

    private sealed class StubRepository(BillTotals? totals = null) : IBillRepository
    {
        public List<BillQuery> ReceivedQueries { get; } = [];

        public Task AddAsync(Bill bill, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Bill bill, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Bill?>(null);

        public Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bill>>([]);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<BillTotals?> ComputeTotalsAsync(BillQuery query, CancellationToken ct = default)
        {
            ReceivedQueries.Add(query);
            return Task.FromResult(totals);
        }
    }

    private static BillChunk Chunk(string utility, string text, DateOnly start, DateOnly end, string file = "bill.pdf") => new()
    {
        BillId = Guid.NewGuid().ToString(),
        Text = text,
        Utility = utility,
        FileName = file,
        PageNumber = 1,
        PeriodStartDay = start.DayNumber,
        PeriodEndDay = end.DayNumber,
    };

    private static BillChatService Build(IBillChunkStore store, IBillRepository repository, FakeChatClient? client = null) =>
        new(client ?? new FakeChatClient("Your July electricity bill was $220.57. [1]"),
            new FakeEmbeddingGenerator(),
            store,
            repository,
            new FakeTimeProvider(Now),
            NullLogger<BillChatService>.Instance);

    [Fact]
    public async Task AnswerCitesTheChunksItWasGiven()
    {
        var store = new StubChunkStore(Chunk("Electricity", "TOTAL AMOUNT DUE $220.57", new(2025, 7, 1), new(2025, 7, 31), "electric-2025-07.pdf"));

        var answer = await Build(store, new StubRepository()).AskAsync(new ChatRequest("What was my electricity bill?"));

        Assert.Contains("220.57", answer.Answer);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal("electric-2025-07.pdf", citation.FileName);
        Assert.Equal(1, citation.PageNumber);
    }

    [Fact]
    public async Task WithNothingRetrieved_ItSaysSoInsteadOfAskingTheModel()
    {
        var client = new FakeChatClient("this should never be sent");
        var answer = await Build(new StubChunkStore(), new StubRepository(), client).AskAsync(new ChatRequest("What about my sewage bill?"));

        Assert.Empty(answer.Citations);
        Assert.Equal(0, client.CallCount);
        Assert.Contains("could not find", answer.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NumericQuestions_GetExactTotalsFromTheDatabase()
    {
        var totals = new BillTotals(3, 481.71m, "USD", 1654, "kWh", new(2025, 5, 1), new(2025, 7, 31));
        var repository = new StubRepository(totals);
        var store = new StubChunkStore(Chunk("Electricity", "charges", new(2025, 7, 1), new(2025, 7, 31)));

        var answer = await Build(store, repository).AskAsync(new ChatRequest("How much did I spend on electricity altogether in 2025?"));

        Assert.NotNull(answer.Totals);
        Assert.Equal(481.71m, answer.Totals!.TotalAmount);
        Assert.Single(repository.ReceivedQueries);
        Assert.Equal(UtilityKind.Electricity, repository.ReceivedQueries[0].Utility);
    }

    [Fact]
    public async Task NonNumericQuestions_DoNotQueryTotals()
    {
        var repository = new StubRepository(new BillTotals(1, 10m, "USD", null, null, null, null));
        var store = new StubChunkStore(Chunk("Gas", "Payment due date 2025-08-20", new(2025, 7, 1), new(2025, 7, 31)));

        var answer = await Build(store, repository).AskAsync(new ChatRequest("When is my gas bill due?"));

        Assert.Empty(repository.ReceivedQueries);
        Assert.Null(answer.Totals);
    }

    [Fact]
    public async Task TotalsAreGivenToTheModel_SoItDoesNotDoItsOwnArithmetic()
    {
        var client = new FakeChatClient("ok");
        var totals = new BillTotals(3, 481.71m, "USD", null, null, null, null);
        var store = new StubChunkStore(Chunk("Electricity", "charges", new(2025, 7, 1), new(2025, 7, 31)));

        await Build(store, new StubRepository(totals), client).AskAsync(new ChatRequest("What did I spend in total on electricity?"));

        var prompt = string.Join("\n", client.ReceivedMessages[0].Select(m => m.Text));
        Assert.Contains("<totals>", prompt);
        Assert.Contains("481.71", prompt);
    }

    [Fact]
    public async Task ThePerPeriodSeriesReachesThePrompt_SoTrendsCanBeRead()
    {
        var client = new FakeChatClient("ok");
        var totals = new BillTotals(2, 365.77m, "USD", 33, "CCF", new(2025, 4, 1), new(2025, 9, 30),
        [
            new PeriodTotal(new(2025, 4, 1), new(2025, 6, 30), 159.53m, 14, "CCF"),
            new PeriodTotal(new(2025, 7, 1), new(2025, 9, 30), 206.24m, 19, "CCF"),
        ]);

        var store = new StubChunkStore(Chunk("Water", "Total usage 19 CCF", new(2025, 7, 1), new(2025, 9, 30)));

        await Build(store, new StubRepository(totals), client).AskAsync(new ChatRequest("Is my water usage going up?"));

        // The system prompt also mentions "By period", so inspect the grounded user message alone.
        var prompt = client.ReceivedMessages[0].Last(m => m.Role == ChatRole.User).Text;

        // The direction is stated as a computed fact, not left for the model to work out.
        Assert.Contains("Direction: ", prompt);
        Assert.Contains("usage rose 36% from 14 to 19 CCF", prompt);

        // And the periods are listed, oldest first, so the figures can be checked.
        var list = prompt[prompt.IndexOf("By period (oldest first):", StringComparison.Ordinal)..];
        Assert.True(list.IndexOf("14 CCF", StringComparison.Ordinal) < list.IndexOf("19 CCF", StringComparison.Ordinal),
            "the older period must be listed first, or the direction reads backwards");
    }

    [Fact]
    public async Task ASingleMatchingBill_NeedsNoSeries()
    {
        var client = new FakeChatClient("ok");
        var totals = new BillTotals(1, 220.57m, "USD", 734, "kWh", new(2025, 7, 1), new(2025, 7, 31),
            [new PeriodTotal(new(2025, 7, 1), new(2025, 7, 31), 220.57m, 734, "kWh")]);

        var store = new StubChunkStore(Chunk("Electricity", "charges", new(2025, 7, 1), new(2025, 7, 31)));

        await Build(store, new StubRepository(totals), client).AskAsync(new ChatRequest("What did I spend in total?"));

        var prompt = client.ReceivedMessages[0].Last(m => m.Role == ChatRole.User).Text;
        Assert.DoesNotContain("By period", prompt);
    }

    [Fact]
    public async Task AGuessedYearIsWalkedBack_WhenThatWindowHoldsNoBills()
    {
        // "in July" means July 2026 today, but the only July bill is from 2025.
        var store = new StubChunkStore(Chunk("Electricity", "TOTAL AMOUNT DUE $220.57", new(2025, 7, 1), new(2025, 7, 31)));

        var answer = await Build(store, new StubRepository()).AskAsync(new ChatRequest("How much did I pay for electricity in July?"));

        Assert.NotEmpty(answer.Citations);
        Assert.Equal(new DateOnly(2026, 7, 1), store.ReceivedFilters[0]!.From);
        Assert.Equal(new DateOnly(2025, 7, 1), store.ReceivedFilters[1]!.From);
    }

    [Fact]
    public async Task AnExplicitDateFilterIsNeverSecondGuessed()
    {
        var store = new StubChunkStore(Chunk("Electricity", "charges", new(2025, 7, 1), new(2025, 7, 31)));

        var answer = await Build(store, new StubRepository()).AskAsync(
            new ChatRequest("How much did I pay in July?", From: new DateOnly(2026, 7, 1), To: new DateOnly(2026, 7, 31)));

        Assert.Single(store.ReceivedFilters);
        Assert.Empty(answer.Citations);
    }

    [Fact]
    public async Task StreamingSendsSourcesFirst_ThenTokens_ThenDone()
    {
        var store = new StubChunkStore(Chunk("Water", "Total usage 19 CCF", new(2025, 7, 1), new(2025, 9, 30)));

        var events = new List<ChatStreamEvent>();
        await foreach (var e in Build(store, new StubRepository()).StreamAsync(new ChatRequest("How much water did I use?")))
        {
            events.Add(e);
        }

        Assert.Equal("sources", events[0].Type);
        Assert.NotEmpty(events[0].Citations!);
        Assert.Contains(events, e => e.Type == "token");
        Assert.Equal("done", events[^1].Type);
    }

    [Fact]
    public async Task TopKIsCappedSoThePromptCannotOverrunTheContextWindow()
    {
        var chunks = Enumerable.Range(0, 40)
            .Select(i => Chunk("Electricity", $"line {i}", new(2025, 7, 1), new(2025, 7, 31)))
            .ToArray();

        var store = new StubChunkStore(chunks);

        var answer = await Build(store, new StubRepository()).AskAsync(new ChatRequest("What are my charges?", TopK: 500));

        Assert.True(answer.Citations.Count <= 12, $"expected at most 12 citations, got {answer.Citations.Count}");
    }

    [Fact]
    public async Task AnEmptyQuestionIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Build(new StubChunkStore(), new StubRepository()).AskAsync(new ChatRequest("   ")));
    }
}
