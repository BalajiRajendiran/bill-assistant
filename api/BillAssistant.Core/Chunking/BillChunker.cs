using System.Text.RegularExpressions;
using BillAssistant.Core.Models;

namespace BillAssistant.Core.Chunking;

/// <summary>
/// Splits bill text into overlapping chunks along paragraph and line boundaries.
/// </summary>
/// <remarks>
/// Two rules matter for this domain:
/// <list type="bullet">
/// <item>Chunks never split mid-line. Utility bills keep the figures that matter in rate tables, and a
/// boundary through a table row ("Tier 2  0.243  x  180 kWh") strands a number from its label, which
/// reads as authoritative to the model and produces confidently wrong answers.</item>
/// <item>Table-like blocks are kept whole where they fit, so a rate table survives as one passage.</item>
/// </list>
/// Pure and dependency-free, so it is fully unit-testable without Ollama or Qdrant.
/// </remarks>
public sealed partial class BillChunker(ChunkingOptions? options = null)
{
    private readonly ChunkingOptions _options = Validated(options ?? new ChunkingOptions());

    private static ChunkingOptions Validated(ChunkingOptions o)
    {
        o.Validate();
        return o;
    }

    public IReadOnlyList<TextChunk> Chunk(PdfDocumentText document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var chunks = new List<TextChunk>();
        var index = 0;

        foreach (var page in document.Pages)
        {
            foreach (var text in ChunkPage(page.Text))
            {
                chunks.Add(new TextChunk(page.PageNumber, index++, text));
            }
        }

        return chunks;
    }

    private IEnumerable<string> ChunkPage(string pageText)
    {
        var blocks = SplitIntoBlocks(pageText);
        if (blocks.Count == 0)
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            var text = string.Join('\n', current).Trim();
            if (text.Length > 0)
            {
                chunks.Add(text);
            }

            var carry = TailLines(current, _options.OverlapChars);
            current = carry;
            currentLength = Length(carry);
        }

        foreach (var block in blocks)
        {
            // An oversized block (a long table, or a wall of terms and conditions) is broken at line
            // boundaries only - never inside a line.
            var pieces = block.Length <= _options.MaxChars ? [block] : SplitOversized(block, _options.MaxChars);

            foreach (var piece in pieces)
            {
                var separator = current.Count > 0 ? 1 : 0; // blank line between blocks
                if (currentLength > 0 && currentLength + separator + piece.Length > _options.MaxChars)
                {
                    Flush();
                }

                if (current.Count > 0)
                {
                    current.Add(string.Empty);
                    currentLength += 1;
                }

                current.AddRange(piece.Lines);
                currentLength += piece.Length;
            }
        }

        // Final flush, without seeding an overlap that nothing follows.
        var tail = string.Join('\n', current).Trim();
        if (tail.Length > 0)
        {
            chunks.Add(tail);
        }

        return FoldShortTail(chunks);
    }

    /// <summary>A trailing sliver carries no useful context on its own; fold it into its predecessor.</summary>
    private List<string> FoldShortTail(List<string> chunks)
    {
        if (chunks.Count > 1 && chunks[^1].Length < _options.MinChars)
        {
            chunks[^2] = chunks[^2] + "\n" + chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
        }

        return chunks;
    }

    /// <summary>Takes whole lines from the end of a chunk, up to <paramref name="budget"/> characters.</summary>
    private static List<string> TailLines(List<string> lines, int budget)
    {
        if (budget <= 0)
        {
            return [];
        }

        var taken = new List<string>();
        var total = 0;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var length = lines[i].Length + 1;
            if (total + length > budget)
            {
                break;
            }

            taken.Insert(0, lines[i]);
            total += length;
        }

        // Never start a chunk with a blank line left over from a block separator.
        while (taken.Count > 0 && taken[0].Length == 0)
        {
            taken.RemoveAt(0);
        }

        return taken;
    }

    /// <summary>
    /// Breaks a block that exceeds the chunk budget, at line boundaries only. When the block is a table,
    /// its header line is repeated at the top of every piece - a rate table split across two chunks is
    /// useless if only the first half knows what the columns mean.
    /// </summary>
    private static List<Block> SplitOversized(Block block, int maxChars)
    {
        var header = block.IsTable && block.Lines.Count > 1 ? block.Lines[0] : null;
        var pieces = new List<Block>();
        var lines = new List<string>();
        var length = 0;

        void Start()
        {
            lines = [];
            length = 0;
            if (header is not null && pieces.Count > 0)
            {
                lines.Add(header);
                length += header.Length + 1;
            }
        }

        foreach (var line in block.Lines)
        {
            var lineLength = line.Length + 1;
            if (lines.Count > 0 && length + lineLength > maxChars)
            {
                pieces.Add(new Block(lines, block.IsTable));
                Start();
            }

            lines.Add(line);
            length += lineLength;
        }

        if (lines.Count > 0)
        {
            pieces.Add(new Block(lines, block.IsTable));
        }

        return pieces;
    }

    private static List<Block> SplitIntoBlocks(string pageText)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return blocks;
        }

        var lines = pageText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var buffer = new List<string>();

        void Close()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            blocks.Add(new Block([.. buffer], LooksTabular(buffer)));
            buffer.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Close();
                continue;
            }

            buffer.Add(line);
        }

        Close();
        return blocks;
    }

    /// <summary>
    /// A block reads as tabular when most of its lines are a label followed by column-aligned values -
    /// the shape of the usage and rate tables on a utility bill.
    /// </summary>
    private static bool LooksTabular(List<string> lines)
    {
        if (lines.Count < 2)
        {
            return false;
        }

        var tabular = lines.Count(TableRowPattern().IsMatch);
        return tabular * 2 >= lines.Count;
    }

    [GeneratedRegex(@"(\s{2,}|\t).*(\d|\$|%)", RegexOptions.CultureInvariant)]
    private static partial Regex TableRowPattern();

    private sealed record Block(List<string> Lines, bool IsTable)
    {
        public int Length { get; } = BillChunker.Length(Lines);
    }

    private static int Length(List<string> lines)
    {
        var total = 0;
        foreach (var line in lines)
        {
            total += line.Length + 1;
        }

        return total;
    }
}
