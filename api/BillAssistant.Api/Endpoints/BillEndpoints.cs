using BillAssistant.Api.Contracts;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Validation;

namespace BillAssistant.Api.Endpoints;

/// <summary>Upload, list, inspect and delete bills.</summary>
public static class BillEndpoints
{
    /// <summary>Generous enough for a scanned-quality statement, small enough to reject nonsense early.</summary>
    private const long MaxUploadBytes = 25 * 1024 * 1024;

    public static RouteGroupBuilder MapBillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bills").WithTags("Bills");

        group.MapPost("/", UploadAsync)
            .DisableAntiforgery()
            .WithName("UploadBill")
            .WithSummary("Upload a bill PDF and index it.");

        group.MapGet("/", ListAsync)
            .WithName("ListBills")
            .WithSummary("List ingested bills, most recent period first.");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetBill")
            .WithSummary("Get one bill.");

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteBill")
            .WithSummary("Delete a bill and its indexed chunks.");

        return group;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile? file,
        IBillIngestionService ingestion,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.BadRequest(new { error = "Attach a PDF in the 'file' field." });
        }

        if (file.Length > MaxUploadBytes)
        {
            return TypedResults.BadRequest(new { error = $"The file is larger than the {MaxUploadBytes / (1024 * 1024)}MB limit." });
        }

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest(new { error = "Only PDF files can be ingested." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await ingestion.IngestAsync(stream, Path.GetFileName(file.FileName), ct);

            return TypedResults.Created(
                $"/api/bills/{result.Bill.Id}",
                new IngestResponse(BillResponse.Create(result.Bill), result.ChunksIndexed, result.Warning));
        }
        catch (UnreadablePdfException ex)
        {
            // A scan or a corrupt file is the user's problem to fix, so say what is wrong.
            return TypedResults.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListAsync(
        IBillRepository repository,
        string? utility,
        DateOnly? from,
        DateOnly? to,
        int? limit,
        CancellationToken ct)
    {
        var kind = string.IsNullOrWhiteSpace(utility) ? null : (UtilityKind?)BillMetadataValidator.ParseUtility(utility);
        if (kind == UtilityKind.Unknown)
        {
            return TypedResults.BadRequest(new { error = $"Unknown utility '{utility}'." });
        }

        var bills = await repository.ListAsync(new BillQuery(kind, from, to, Math.Clamp(limit ?? 200, 1, 500)), ct);

        return TypedResults.Ok(bills.Select(BillResponse.Create).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, IBillRepository repository, CancellationToken ct)
    {
        var bill = await repository.GetAsync(id, ct);
        return bill is null ? TypedResults.NotFound() : TypedResults.Ok(BillResponse.Create(bill));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IBillRepository repository,
        IBillChunkStore chunkStore,
        CancellationToken ct)
    {
        // Chunks go first: an orphaned row is harmless, an orphaned vector would keep being cited.
        await chunkStore.DeleteByBillAsync(id, ct);
        var deleted = await repository.DeleteAsync(id, ct);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
