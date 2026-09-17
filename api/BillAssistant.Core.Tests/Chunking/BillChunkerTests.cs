using BillAssistant.Core.Chunking;
using BillAssistant.Core.Models;

namespace BillAssistant.Core.Tests.Chunking;

public class BillChunkerTests
{
    private static PdfDocumentText Doc(params string[] pages) =>
        new([.. pages.Select((t, i) => new PdfPageText(i + 1, t))]);

    [Fact]
    public void EmptyPage_ProducesNoChunks()
    {
        var chunks = new BillChunker().Chunk(Doc("   \n\n  "));
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkIndices_AreSequentialAcrossPages()
    {
        var page = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i} " + new string('x', 200)));
        var chunks = new BillChunker().Chunk(Doc(page, page));

        Assert.True(chunks.Count > 2);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.ChunkIndex));
        Assert.Contains(chunks, c => c.PageNumber == 1);
        Assert.Contains(chunks, c => c.PageNumber == 2);
    }

    [Fact]
    public void Chunks_StayWithinBudget_ExceptForOversizedSingleLines()
    {
        var options = new ChunkingOptions { MaxChars = 400, OverlapChars = 60, MinChars = 40 };
        var page = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Line {i}: " + new string('y', 80)));

        var chunks = new BillChunker(options).Chunk(Doc(page));

        Assert.All(chunks, c => Assert.True(c.Text.Length <= options.MaxChars,
            $"chunk of {c.Text.Length} chars exceeded budget of {options.MaxChars}"));
    }

    [Fact]
    public void NeverSplitsInsideALine()
    {
        // Every line is uniquely identifiable; if a chunk boundary fell mid-line, one of these
        // lines would appear truncated in every chunk that contains it.
        var lines = Enumerable.Range(0, 60).Select(i => $"ROW-{i:D3} | Tier {i} | 0.24 | {i * 13} kWh | ${i * 2}.50").ToArray();
        var chunks = new BillChunker(new ChunkingOptions { MaxChars = 300, OverlapChars = 50, MinChars = 20 })
            .Chunk(Doc(string.Join("\n", lines)));

        var emitted = chunks.SelectMany(c => c.Text.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct();

        Assert.All(emitted, line => Assert.Contains(line, lines));
    }

    [Fact]
    public void TableThatFits_IsKeptInOneChunk()
    {
        var table = """
            Usage summary
            Tier 1        0.1802    x   300 kWh    $54.06
            Tier 2        0.2431    x   112 kWh    $27.23
            Tier 3        0.3109    x    18 kWh     $5.60
            """;
        var page = "Account summary paragraph.\n\n" + table + "\n\nThank you for your business.";

        var chunks = new BillChunker(new ChunkingOptions { MaxChars = 1200 }).Chunk(Doc(page));

        var withTable = chunks.Where(c => c.Text.Contains("Tier 1")).ToList();
        Assert.Single(withTable);
        Assert.Contains("Tier 2", withTable[0].Text);
        Assert.Contains("Tier 3", withTable[0].Text);
    }

    [Fact]
    public void OversizedTable_RepeatsHeaderOnEveryPiece()
    {
        var rows = Enumerable.Range(1, 40).Select(i => $"Day {i,-4}   {i * 3,5} kWh   ${i * 2}.10");
        var page = "Daily usage detail   Units   Charge\n" + string.Join("\n", rows);

        var chunks = new BillChunker(new ChunkingOptions { MaxChars = 400, OverlapChars = 40, MinChars = 30 })
            .Chunk(Doc(page));

        var pieces = chunks.Where(c => c.Text.Contains(" kWh")).ToList();
        Assert.True(pieces.Count > 1, "expected the table to be split across several chunks");
        Assert.All(pieces, p => Assert.Contains("Daily usage detail", p.Text));
    }

    [Fact]
    public void ConsecutiveChunks_Overlap()
    {
        var options = new ChunkingOptions { MaxChars = 400, OverlapChars = 120, MinChars = 40 };
        var page = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i} about charges and credits."));

        var chunks = new BillChunker(options).Chunk(Doc(page));

        Assert.True(chunks.Count > 1);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousLines = chunks[i - 1].Text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            var currentLines = chunks[i].Text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            Assert.Contains(currentLines[0], previousLines);
        }
    }

    [Fact]
    public void ShortTrailingChunk_IsFoldedIntoPrevious()
    {
        var options = new ChunkingOptions { MaxChars = 300, OverlapChars = 0, MinChars = 120 };
        var page = new string('a', 290) + "\n\nBye.";

        var chunks = new BillChunker(options).Chunk(Doc(page));

        Assert.DoesNotContain(chunks, c => c.Text.Trim() == "Bye.");
        Assert.Contains("Bye.", chunks[^1].Text);
    }

    [Theory]
    [InlineData(100, 20, 10)]   // MaxChars too small
    [InlineData(400, 200, 10)]  // overlap >= half of max
    [InlineData(400, 20, 400)]  // min >= max
    public void InvalidOptions_AreRejected(int max, int overlap, int min)
    {
        var options = new ChunkingOptions { MaxChars = max, OverlapChars = overlap, MinChars = min };
        Assert.Throws<ArgumentOutOfRangeException>(() => new BillChunker(options));
    }
}
