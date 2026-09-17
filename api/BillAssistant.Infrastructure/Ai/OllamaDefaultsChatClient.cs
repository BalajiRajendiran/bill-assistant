using Microsoft.Extensions.AI;
using OllamaSharp.Models;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;

namespace BillAssistant.Infrastructure.Ai;

/// <summary>
/// Applies Ollama-specific request defaults that Microsoft.Extensions.AI has no portable concept for.
/// </summary>
/// <remarks>
/// Exists so that <c>num_ctx</c> can be set without any caller knowing Ollama is the provider. Ollama
/// defaults the context window to 4096 tokens regardless of the model's advertised capacity, and an
/// over-long RAG prompt is truncated from the front - dropping the system instructions while leaving
/// the excerpts, which produces an ungrounded answer that looks fine. Uses the sanctioned
/// <see cref="ChatOptions.RawRepresentationFactory"/> hook and never overwrites one a caller set.
/// </remarks>
internal sealed class OllamaDefaultsChatClient(IChatClient innerClient, int numCtx) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, WithDefaults(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, WithDefaults(options), cancellationToken);

    private ChatOptions WithDefaults(ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();

        result.RawRepresentationFactory ??= _ => new OllamaChatRequest
        {
            Options = new RequestOptions { NumCtx = numCtx }
        };

        return result;
    }
}
