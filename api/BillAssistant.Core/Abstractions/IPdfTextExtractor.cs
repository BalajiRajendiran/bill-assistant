using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>Pulls text out of a PDF. Text-layer only - scanned bills are rejected, not OCR'd.</summary>
public interface IPdfTextExtractor
{
    PdfDocumentText Extract(Stream pdfStream);
}

/// <summary>Thrown when a PDF has no usable text layer (typically a scan or a photo).</summary>
public sealed class UnreadablePdfException(string message) : Exception(message);
