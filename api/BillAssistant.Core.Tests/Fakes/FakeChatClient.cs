using Microsoft.Extensions.AI;

namespace BillAssistant.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IChatClient"/> that replays canned replies, so the pipeline can be tested without
/// Ollama running and without a model's variability.
/// </summary>
public sealed class FakeChatClient(params string[] replies) : IChatClient
{
    private int _call;

    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    public List<ChatOptions?> ReceivedOptions { get; } = [];

    public int CallCount => _call;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add([.. messages]);
        ReceivedOptions.Add(options);

        var reply = replies.Length == 0 ? string.Empty : replies[Math.Min(_call, replies.Length - 1)];
        _call++;

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var word in response.Text.Split(' '))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
