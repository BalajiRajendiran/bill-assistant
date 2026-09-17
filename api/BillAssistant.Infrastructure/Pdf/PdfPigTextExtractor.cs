using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BillAssistant.Infrastructure.Pdf;

/// <summary>
/// Extracts a PDF's text layer with PdfPig.
/// </summary>
/// <remarks>
/// Uses <see cref="ContentOrderTextExtractor"/> rather than <c>page.Text</c>. The latter returns glyphs
/// in PDF content-stream order, which on a two-column utility bill interleaves the address block with
/// the charges table and produces text no model can read correctly.
/// </remarks>
public sealed class PdfPigTextExtractor(ILogger<PdfPigTextExtractor> logger) : IPdfTextExtractor
{
    /// <summary>Below this many characters per page on average, we assume there is no real text layer.</summary>
    private const int MinCharactersPerPage = 40;

    private static readonly ContentOrderTextExtractor.Options ExtractorOptions = new()
    {
        SeparateParagraphsWithDoubleNewline = true,
        ReplaceWhitespaceWithSpace = false
    };

    public PdfDocumentText Extract(Stream pdfStream)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);

        // PdfPig needs a seekable stream it can read repeatedly.
        using var buffer = new MemoryStream();
        pdfStream.CopyTo(buffer);
        buffer.Position = 0;

        if (buffer.Length == 0)
        {
            throw new UnreadablePdfException("The uploaded file is empty.");
        }

        var pages = new List<PdfPageText>();

        try
        {
            using var document = PdfDocument.Open(buffer);

            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page, ExtractorOptions) ?? string.Empty;
                pages.Add(new PdfPageText(page.Number, Normalise(text)));
            }
        }
        catch (UnreadablePdfException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PdfPig could not open the uploaded file.");
            throw new UnreadablePdfException("The file could not be read as a PDF.");
        }

        if (pages.Count == 0)
        {
            throw new UnreadablePdfException("The PDF has no pages.");
        }

        var extracted = new PdfDocumentText(pages);

        // A scan or a photo of a bill opens fine and yields almost no characters. Say so plainly
        // instead of indexing a handful of empty chunks that will never match anything.
        if (extracted.TotalCharacters < MinCharactersPerPage * pages.Count)
        {
            throw new UnreadablePdfException(
                $"This PDF has little or no text layer ({extracted.TotalCharacters} characters across {pages.Count} page(s)). " +
                "It is most likely a scan; OCR is not supported.");
        }

        return extracted;
    }

    /// <summary>
    /// Normalises line endings and collapses runs of blank lines, so the chunker sees a consistent
    /// paragraph structure regardless of how the producing application laid the page out.
    /// </summary>
    private static string Normalise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>(lines.Length);
        var blankRun = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (++blankRun > 1)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            result.Add(line);
        }

        return string.Join('\n', result).Trim();
    }
}
