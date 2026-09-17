using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Chunking;
using BillAssistant.Infrastructure.Ai;
using BillAssistant.Infrastructure.Ingestion;
using BillAssistant.Infrastructure.Options;
using BillAssistant.Infrastructure.Pdf;
using BillAssistant.Infrastructure.Persistence;
using BillAssistant.Infrastructure.Retrieval;
using BillAssistant.Infrastructure.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace BillAssistant.Infrastructure;

/// <summary>Registers every adapter the API needs. The API project itself wires up nothing vendor-specific.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBillAssistantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAiProvider(configuration);

        services.AddOptions<VectorStoreOptions>()
            .Bind(configuration.GetSection(VectorStoreOptions.SectionName))
            .ValidateOnStart();

        var vectorOptions = configuration.GetSection(VectorStoreOptions.SectionName).Get<VectorStoreOptions>() ?? new VectorStoreOptions();
        var aiOptions = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        var active = aiOptions.Active;

        // Relational store for bill metadata.
        var connectionString = configuration.GetConnectionString("Bills") ?? "Data Source=bills.db";
        services.AddDbContext<BillDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IBillRepository, EfBillRepository>();

        // Vector store. Swapping this for CommunityToolkit.VectorData.AzureAISearch (or .InMemory, as
        // the tests do) is a one-line change here - everything downstream sees VectorStore.
        services.AddQdrantVectorStore(
            vectorOptions.Host,
            vectorOptions.Port,
            vectorOptions.Https,
            string.IsNullOrWhiteSpace(vectorOptions.ApiKey) ? null! : vectorOptions.ApiKey);

        services.AddSingleton<IBillChunkStore>(sp => new VectorDataBillChunkStore(
            sp.GetRequiredService<VectorStore>(),
            BillChunkSchema.CollectionName(vectorOptions.CollectionPrefix, active.EmbeddingModel),
            active.EmbeddingDimensions,
            sp.GetRequiredService<ILogger<VectorDataBillChunkStore>>()));

        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton(new BillChunker());
        services.AddSingleton<BillMetadataExtractor>();
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IBillIngestionService, BillIngestionService>();
        services.AddScoped<IBillChatService, BillChatService>();

        return services;
    }

    /// <summary>Applies pending EF migrations. Called at startup in development.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillDbContext>();
        await db.Database.MigrateAsync(ct);
    }
}
