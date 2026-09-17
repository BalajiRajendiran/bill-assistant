using System.ComponentModel.DataAnnotations;

namespace BillAssistant.Infrastructure.Options;

/// <summary>Which model provider to use, and how to reach it. Bound from the "Ai" configuration section.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>"Ollama" or "AzureOpenAI".</summary>
    [Required]
    public string Provider { get; set; } = AiProviders.Ollama;

    public OllamaOptions Ollama { get; set; } = new();

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();

    /// <summary>
    /// Settings for the active provider, whichever it is. Everything downstream reads dimensions and
    /// the embedding model name from here, so neither has to know which vendor answered.
    /// </summary>
    public ProviderSettings Active =>
        IsAzure
            ? new ProviderSettings(AzureOpenAI.ChatDeployment, AzureOpenAI.EmbeddingDeployment, AzureOpenAI.EmbeddingDimensions)
            : new ProviderSettings(Ollama.ChatModel, Ollama.EmbeddingModel, Ollama.EmbeddingDimensions);

    public bool IsAzure => string.Equals(Provider, AiProviders.AzureOpenAI, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!string.Equals(Provider, AiProviders.Ollama, StringComparison.OrdinalIgnoreCase) && !IsAzure)
        {
            throw new InvalidOperationException(
                $"Ai:Provider must be '{AiProviders.Ollama}' or '{AiProviders.AzureOpenAI}', but was '{Provider}'.");
        }

        if (IsAzure)
        {
            Require(AzureOpenAI.Endpoint, "Ai:AzureOpenAI:Endpoint");
            Require(AzureOpenAI.ChatDeployment, "Ai:AzureOpenAI:ChatDeployment");
            Require(AzureOpenAI.EmbeddingDeployment, "Ai:AzureOpenAI:EmbeddingDeployment");
        }
        else
        {
            Require(Ollama.Endpoint, "Ai:Ollama:Endpoint");
            Require(Ollama.ChatModel, "Ai:Ollama:ChatModel");
            Require(Ollama.EmbeddingModel, "Ai:Ollama:EmbeddingModel");
        }

        if (Active.EmbeddingDimensions <= 0)
        {
            throw new InvalidOperationException("The active provider's EmbeddingDimensions must be greater than zero.");
        }
    }

    private static void Require(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Configuration '{key}' is required for the selected provider.");
        }
    }
}

public static class AiProviders
{
    public const string Ollama = "Ollama";
    public const string AzureOpenAI = "AzureOpenAI";
}

/// <summary>Provider-neutral view of the active configuration.</summary>
public sealed record ProviderSettings(string ChatModel, string EmbeddingModel, int EmbeddingDimensions);

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string ChatModel { get; set; } = "llama3.2";

    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>nomic-embed-text produces 768-dimension vectors.</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>
    /// Context window for chat requests. Ollama defaults to 4096 no matter what the model advertises,
    /// and silently drops the front of an over-long prompt - which is where the system instructions are.
    /// </summary>
    public int NumCtx { get; set; } = 8192;

    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class AzureOpenAIOptions
{
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Leave empty to authenticate with DefaultAzureCredential instead of a key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string ChatDeployment { get; set; } = "gpt-4o-mini";

    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>text-embedding-3-small produces 1536-dimension vectors.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;
}
