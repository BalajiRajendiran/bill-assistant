namespace BillAssistant.Infrastructure.Options;

/// <summary>Qdrant connection settings. Bound from the "VectorStore" configuration section.</summary>
public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public string Host { get; set; } = "localhost";

    /// <summary>gRPC port. Qdrant's REST API is on 6333; the .NET client speaks gRPC on 6334.</summary>
    public int Port { get; set; } = 6334;

    public bool Https { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base collection name; the embedding model is appended to it at runtime.</summary>
    public string CollectionPrefix { get; set; } = "bill_chunks";
}
