namespace BillAssistant.Core.Models;

/// <summary>Text of a single PDF page, in reading order.</summary>
public sealed record PdfPageText(int PageNumber, string Text);

/// <summary>All extracted text for a PDF.</summary>
public sealed record PdfDocumentText(IReadOnlyList<PdfPageText> Pages)
{
    public int TotalCharacters => Pages.Sum(p => p.Text.Length);
}
