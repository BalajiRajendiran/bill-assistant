using System.Globalization;
using BillAssistant.Core.Models;

namespace BillAssistant.Core.Validation;

/// <summary>
/// Turns a model-produced <see cref="BillMetadataDraft"/> into validated <see cref="BillMetadata"/>.
/// </summary>
/// <remarks>
/// llama3.2 is a 3B model and is the weakest link in this pipeline: it will return "2025-13-45",
/// negative totals, or a period that ends before it starts. Everything it produces is parsed and
/// range-checked here, and anything that fails is dropped with a note rather than persisted - a wrong
/// number in the bills table would silently corrupt every total computed from it.
/// </remarks>
public static class BillMetadataValidator
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d MMM yyyy", "MMM d, yyyy", "MMMM d, yyyy"
    ];

    /// <summary>Bills older or newer than this are treated as a hallucinated date.</summary>
    private static readonly DateOnly MinPlausibleDate = new(2000, 1, 1);

    public static BillMetadata Validate(BillMetadataDraft draft, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var issues = new List<string>();
        var maxPlausibleDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2));

        var utility = ParseUtility(draft.Utility);
        if (utility == UtilityKind.Unknown && !string.IsNullOrWhiteSpace(draft.Utility))
        {
            issues.Add($"Unrecognised utility '{draft.Utility}'.");
        }

        var periodStart = ParseDate(draft.PeriodStart, "PeriodStart", maxPlausibleDate, issues);
        var periodEnd = ParseDate(draft.PeriodEnd, "PeriodEnd", maxPlausibleDate, issues);
        var dueDate = ParseDate(draft.DueDate, "DueDate", maxPlausibleDate, issues);

        if (periodStart is { } start && periodEnd is { } end && start > end)
        {
            issues.Add($"Service period ends ({end:yyyy-MM-dd}) before it starts ({start:yyyy-MM-dd}).");
            periodStart = null;
            periodEnd = null;
        }

        decimal? amount = null;
        if (draft.AmountDue is { } rawAmount)
        {
            if (double.IsNaN(rawAmount) || double.IsInfinity(rawAmount) || rawAmount < 0 || rawAmount > 1_000_000)
            {
                issues.Add($"Implausible amount due: {rawAmount}.");
            }
            else
            {
                amount = Math.Round((decimal)rawAmount, 2);
            }
        }

        double? usage = null;
        if (draft.UsageQuantity is { } rawUsage)
        {
            if (double.IsNaN(rawUsage) || double.IsInfinity(rawUsage) || rawUsage < 0)
            {
                issues.Add($"Implausible usage quantity: {rawUsage}.");
            }
            else
            {
                usage = rawUsage;
            }
        }

        problems = issues;

        return new BillMetadata(
            utility,
            Clean(draft.ProviderName),
            RedactAccount(draft.AccountNumber),
            periodStart,
            periodEnd,
            amount,
            NormaliseCurrency(draft.Currency, amount),
            dueDate,
            usage,
            Clean(draft.UsageUnit));
    }

    /// <summary>
    /// Metadata is good enough to aggregate on when we know what kind of bill it is, what it cost, and
    /// which period it covers. Anything less and the bill stays searchable but is flagged for review.
    /// </summary>
    public static bool IsUsable(BillMetadata metadata) =>
        metadata.Utility != UtilityKind.Unknown
        && metadata.AmountDue is not null
        && metadata.PeriodEnd is not null;

    public static UtilityKind ParseUtility(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UtilityKind.Unknown;
        }

        var v = value.Trim().ToLowerInvariant();

        return v switch
        {
            _ when v.Contains("electric") || v.Contains("power") || v.Contains("energy") => UtilityKind.Electricity,
            _ when v.Contains("water") || v.Contains("sewer") => UtilityKind.Water,
            _ when v.Contains("gas") => UtilityKind.Gas,
            _ when v.Contains("internet") || v.Contains("broadband") || v.Contains("fibre") || v.Contains("fiber") => UtilityKind.Internet,
            _ when v.Contains("waste") || v.Contains("trash") || v.Contains("refuse") || v.Contains("recycl") => UtilityKind.Waste,
            "other" => UtilityKind.Other,
            _ => UtilityKind.Unknown
        };
    }

    private static DateOnly? ParseDate(string? value, string field, DateOnly maxPlausibleDate, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (!DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && !DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            issues.Add($"{field} '{text}' is not a date.");
            return null;
        }

        if (parsed < MinPlausibleDate || parsed > maxPlausibleDate)
        {
            issues.Add($"{field} '{text}' is outside the plausible range.");
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// Keeps only the last four characters of an account number. The full number is never needed to
    /// answer a question and does not belong in a database or a prompt.
    /// </summary>
    private static string? RedactAccount(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return null;
        }

        var digits = new string([.. cleaned.Where(char.IsLetterOrDigit)]);
        return digits.Length <= 4 ? digits : digits[^4..];
    }

    private static string? NormaliseCurrency(string? value, decimal? amount)
    {
        var cleaned = Clean(value)?.ToUpperInvariant();
        if (cleaned is null)
        {
            return amount is null ? null : "USD";
        }

        return cleaned switch
        {
            "$" or "US$" or "USD" => "USD",
            "£" or "GBP" => "GBP",
            "€" or "EUR" => "EUR",
            _ when cleaned.Length == 3 && cleaned.All(char.IsLetter) => cleaned,
            _ => "USD"
        };
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}
