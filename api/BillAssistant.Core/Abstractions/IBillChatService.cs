using BillAssistant.Core.Models;

namespace BillAssistant.Core.Abstractions;

/// <summary>Answers questions about the ingested bills, grounded in retrieved chunks.</summary>
public interface IBillChatService
{
    Task<ChatAnswer> AskAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);
}
