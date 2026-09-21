using System.Globalization;
using BillAssistant.Core.Models;

namespace BillAssistant.Core.Retrieval;

/// <summary>
/// Turns a per-period series into one plain sentence about its direction.
/// </summary>
/// <remarks>
/// The same reasoning as computing totals in SQL: a small model given a list of figures and asked
/// "is this going up?" will happily answer from whichever number is nearest to hand - in testing,
/// llama3.2 read "same period last year" out of an excerpt and ignored the series entirely, and on
/// another question asked for a bill it had already been given. Comparing two numbers is arithmetic,
/// so it is done here and stated as a fact the model only has to repeat.
/// </remarks>
public static class TrendSummary
{
    /// <summary>Changes smaller than this read as flat rather than as a trend.</summary>
    private const double FlatThresholdPercent = 2;

    /// <summary>
    /// Describes first-to-last movement, or null when there is nothing to compare.
    /// </summary>
    public static string? Describe(IReadOnlyList<PeriodTotal>? series, string currency)
    {
        if (series is not { Count: > 1 })
        {
            return null;
        }

        // A series covering more than one utility has no direction. "Add the water and electricity
        // bills" matches five bills of two kinds, and first-to-last across them compares an
        // electricity bill in May with a water bill in September - a real percentage computed from
        // two unrelated things, which is worse than no answer because it looks authoritative.
        if (series.Select(p => p.Utility).Distinct().Count() > 1)
        {
            return null;
        }

        var first = series[0];
        var last = series[^1];
        var parts = new List<string>();

        if (Movement((double)first.Amount, (double)last.Amount) is { } amount)
        {
            parts.Add($"amount {amount.Word} {amount.Percent} from {first.Amount.ToString("0.00", CultureInfo.InvariantCulture)} to {last.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency}");
        }

        // Usage only compares when both ends measure the same thing.
        if (first.Usage is { } firstUsage
            && last.Usage is { } lastUsage
            && string.Equals(first.UsageUnit, last.UsageUnit, StringComparison.OrdinalIgnoreCase)
            && Movement(firstUsage, lastUsage) is { } usage)
        {
            parts.Add($"usage {usage.Word} {usage.Percent} from {Number(firstUsage)} to {Number(lastUsage)} {last.UsageUnit}");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return $"{string.Join("; ", parts)} (first period {first.Label}, last period {last.Label}).";
    }

    private static (string Word, string Percent)? Movement(double from, double to)
    {
        if (double.IsNaN(from) || double.IsNaN(to))
        {
            return null;
        }

        if (from <= 0)
        {
            // No meaningful percentage against zero; still worth saying which way it went.
            return to > from ? ("rose", "") : to < from ? ("fell", "") : null;
        }

        var change = (to - from) / from * 100;

        if (Math.Abs(change) < FlatThresholdPercent)
        {
            return ("held steady at about", "");
        }

        var word = change > 0 ? "rose" : "fell";
        return (word, $"{Math.Abs(change).ToString("0", CultureInfo.InvariantCulture)}%");
    }

    private static string Number(double value) =>
        value.ToString(Math.Abs(value % 1) < 0.005 ? "0" : "0.##", CultureInfo.InvariantCulture);
}
