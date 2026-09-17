namespace BillAssistant.Core.Models;

/// <summary>A source passage that an answer was grounded in.</summary>
public sealed record Citation(
    Guid BillId,
    string FileName,
    int PageNumber,
    string Snippet,
    string Utility,
    double? Score);
