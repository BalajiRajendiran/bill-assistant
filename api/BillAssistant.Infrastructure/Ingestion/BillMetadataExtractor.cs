using BillAssistant.Core.Models;
using BillAssistant.Core.Prompts;
using BillAssistant.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace BillAssistant.Infrastructure.Ingestion;

/// <summary>Result of asking the model to read a bill's key facts.</summary>
public sealed record MetadataExtraction(BillMetadata Metadata, MetadataStatus Status, string? Notes);

/// <summary>
/// Pulls structured fields out of a bill using the chat model's JSON-schema response format.
/// </summary>
/// <remarks>
/// This is the weakest link in the pipeline: llama3.2 is a 3B model and structured extraction is the
/// step most likely to want something bigger. It is treated accordingly - one retry, strict validation
/// (see <see cref="BillMetadataValidator"/>), and on failure the bill is stored as
/// <see cref="MetadataStatus.NeedsReview"/> rather than with numbers nobody checked. Chunks are still
/// indexed either way, so the bill remains searchable.
/// </remarks>
public sealed class BillMetadataExtractor(IChatClient chatClient, ILogger<BillMetadataExtractor> logger)
{
    /// <summary>
    /// How much of the bill to show the model. The facts we want are on the first page, and a small
    /// model does better with less noise - the terms and conditions only distract it.
    /// </summary>
    private const int MaxCharacters = 6000;

    public async Task<MetadataExtraction> ExtractAsync(PdfDocumentText document, CancellationToken ct = default)
    {
        var text = BuildExcerpt(document);

        var options = new ChatOptions
        {
            Temperature = 0f, // extraction, not composition: the same bill should give the same answer
            MaxOutputTokens = 500
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BillPrompts.MetadataSystem),
            new(ChatRole.User, BillPrompts.MetadataUser(text))
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            BillMetadataDraft? draft;

            try
            {
                var response = await chatClient.GetResponseAsync<BillMetadataDraft>(messages, options, cancellationToken: ct);
                draft = response.Result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Metadata extraction attempt {Attempt} failed to produce valid JSON.", attempt);
                continue;
            }

            if (draft is null)
            {
                continue;
            }

            var metadata = BillMetadataValidator.Validate(draft, out var problems);

            if (BillMetadataValidator.IsUsable(metadata))
            {
                var notes = problems.Count > 0 ? string.Join(" ", problems) : null;
                return new MetadataExtraction(metadata, MetadataStatus.Extracted, notes);
            }

            logger.LogInformation(
                "Metadata extraction attempt {Attempt} was incomplete: {Problems}",
                attempt,
                problems.Count > 0 ? string.Join(" ", problems) : "required fields missing");

            if (attempt == 2)
            {
                var reason = problems.Count > 0
                    ? string.Join(" ", problems)
                    : "The model did not return a utility type, an amount due and a service period.";

                return new MetadataExtraction(metadata, MetadataStatus.NeedsReview, reason);
            }
        }

        return new MetadataExtraction(
            new BillMetadata(UtilityKind.Unknown, null, null, null, null, null, null, null, null, null),
            MetadataStatus.NeedsReview,
            "The model did not return usable JSON for this bill.");
    }

    /// <summary>Takes the first page in full, then as much of the rest as the budget allows.</summary>
    private static string BuildExcerpt(PdfDocumentText document)
    {
        var text = string.Join("\n\n", document.Pages.Select(p => p.Text));
        return text.Length <= MaxCharacters ? text : text[..MaxCharacters];
    }
}
