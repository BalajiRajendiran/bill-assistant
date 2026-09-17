using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>Turns an uploaded PDF into a stored bill plus embedded, searchable chunks.</summary>
public interface IBillIngestionService
{
    Task<IngestionResult> IngestAsync(Stream pdfStream, string fileName, CancellationToken ct = default);
}
