using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using BillAssistant.Infrastructure.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaSharp;

namespace BillAssistant.Infrastructure.Ai;

/// <summary>
/// The only place in the solution that names a model provider.
/// </summary>
/// <remarks>
/// Everything else depends on <see cref="IChatClient"/> and
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> from Microsoft.Extensions.AI. Moving from Ollama
/// to Azure OpenAI is a change to <c>Ai:Provider</c> in configuration plus the existing case below -
/// no application code changes, because no application code can see the difference.
///
/// Note that swapping providers changes the embedding dimension (768 vs 1536), which invalidates the
/// vector collection. The collection name carries the embedding model for exactly that reason.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();

        var options = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        options.Validate();

        if (options.IsAzure)
        {
            AddAzureOpenAI(services, options.AzureOpenAI);
        }
        else
        {
            AddOllama(services, options.Ollama);
        }

        return services;
    }

    private static void AddOllama(IServiceCollection services, OllamaOptions options)
    {
        var uri = new Uri(options.Endpoint);

        // OllamaApiClient implements IChatClient and IEmbeddingGenerator directly. (The
        // Microsoft.Extensions.AI.Ollama package is deprecated in favour of it.)
        services.AddSingleton(_ => new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) });

        services.AddSingleton<IChatClient>(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            var inner = new OllamaApiClient(http, options.ChatModel);

            return new ChatClientBuilder(new OllamaDefaultsChatClient(inner, options.NumCtx))
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build(sp);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            var inner = new OllamaApiClient(http, options.EmbeddingModel);

            return new EmbeddingGeneratorBuilder<string, Embedding<float>>(inner)
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build(sp);
        });
    }

    private static void AddAzureOpenAI(IServiceCollection services, AzureOpenAIOptions options)
    {
        services.AddSingleton(_ => string.IsNullOrWhiteSpace(options.ApiKey)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey)));

        services.AddSingleton<IChatClient>(sp =>
        {
            var client = sp.GetRequiredService<AzureOpenAIClient>();

            return new ChatClientBuilder(client.GetChatClient(options.ChatDeployment).AsIChatClient())
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build(sp);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var client = sp.GetRequiredService<AzureOpenAIClient>();

            return new EmbeddingGeneratorBuilder<string, Embedding<float>>(
                    client.GetEmbeddingClient(options.EmbeddingDeployment).AsIEmbeddingGenerator(options.EmbeddingDimensions))
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build(sp);
        });
    }
}
