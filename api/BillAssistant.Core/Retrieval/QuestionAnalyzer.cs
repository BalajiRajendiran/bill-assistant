using System.Globalization;
using System.Text.RegularExpressions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Validation;

namespace BillAssistant.Core.Retrieval;

/// <summary>A date window read out of a question.</summary>
public sealed record InferredRange(DateOnly? From, DateOnly? To, bool YearWasGuessed = false);

/// <summary>What a question appears to be asking for.</summary>
/// <param name="YearWasGuessed">
/// True when the question named a month but no year ("in July"), so the year was assumed. The caller
/// may walk the range back a year if that window turns out to hold no bills.
/// </param>
public sealed record QuestionIntent(
    bool WantsTotals,
    UtilityKind? Utility,
    DateOnly? From,
    DateOnly? To,
    bool YearWasGuessed = false);

/// <summary>
/// Reads filters and intent out of a plain-language question.
/// </summary>
/// <remarks>
/// Two jobs. First, spot questions whose answer is arithmetic ("how much did I pay in total last
/// quarter") so they can be answered from SQL instead of by asking the model to add up numbers it
/// read in a chunk - the one thing a small model reliably gets wrong. Second, narrow the vector
/// search: "my July electricity bill" should not retrieve the water bill.
///
/// Deliberately simple and fully deterministic, so it can be unit-tested without a model, and so a
/// wrong guess only widens or narrows retrieval rather than changing an answer.
/// </remarks>
public static partial class QuestionAnalyzer
{
    private static readonly string[] AggregateWords =
    [
        "total", "totals", "altogether", "combined", "sum", "add up", "overall",
        "how much did i pay", "how much have i paid", "how much did i spend", "how much have i spent",
        "average", "per month on average", "spend", "spent", "trend", "trending", "compare", "comparison",

        // Direction-of-travel wording. People ask "is my water usage going up?" far more often than
        // they say "trend", and that question is answered by comparing figures, not by reading one
        // passage - so it belongs on the SQL path too.
        "going up", "going down", "gone up", "gone down", "rising", "falling",
        "increase", "increasing", "decrease", "decreasing", "higher", "lower",
        "more than last", "expensive", "cheaper"
    ];

    public static QuestionIntent Analyse(string question, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(question);

        var text = question.ToLowerInvariant();
        var range = InferRange(text, today);

        return new QuestionIntent(
            WantsTotals: AggregateWords.Any(w => text.Contains(w, StringComparison.Ordinal)),
            Utility: InferUtility(text),
            From: range.From,
            To: range.To,
            YearWasGuessed: range.YearWasGuessed);
    }

    /// <summary>Maps the words a household actually uses onto a utility kind.</summary>
    public static UtilityKind? InferUtility(string text)
    {
        var kind = BillMetadataValidator.ParseUtility(text);
        if (kind != UtilityKind.Unknown)
        {
            return kind;
        }

        // Words that imply a utility without naming it.
        if (text.Contains("kwh", StringComparison.Ordinal) || text.Contains("lighting", StringComparison.Ordinal))
        {
            return UtilityKind.Electricity;
        }

        if (text.Contains("heating", StringComparison.Ordinal) || text.Contains("therm", StringComparison.Ordinal))
        {
            return UtilityKind.Gas;
        }

        return null;
    }

    /// <summary>
    /// Resolves the date words in a question to a concrete range. Returns (null, null) when the
    /// question names no period, which leaves the search across every bill.
    /// </summary>
    public static InferredRange InferRange(string text, DateOnly today)
    {
        if (text.Contains("last year", StringComparison.Ordinal))
        {
            return YearRange(today.Year - 1);
        }

        if (text.Contains("this year", StringComparison.Ordinal) || text.Contains("year to date", StringComparison.Ordinal))
        {
            return new InferredRange(new DateOnly(today.Year, 1, 1), today);
        }

        if (text.Contains("last month", StringComparison.Ordinal))
        {
            var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
            return new InferredRange(start, start.AddMonths(1).AddDays(-1));
        }

        if (text.Contains("this month", StringComparison.Ordinal))
        {
            var start = new DateOnly(today.Year, today.Month, 1);
            return new InferredRange(start, start.AddMonths(1).AddDays(-1));
        }

        if (text.Contains("last quarter", StringComparison.Ordinal))
        {
            var currentQuarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
            var start = currentQuarterStart.AddMonths(-3);
            return new InferredRange(start, currentQuarterStart.AddDays(-1));
        }

        if (text.Contains("this quarter", StringComparison.Ordinal))
        {
            var start = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
            return new InferredRange(start, start.AddMonths(3).AddDays(-1));
        }

        var lastN = LastMonthsPattern().Match(text);
        if (lastN.Success && int.TryParse(lastN.Groups[1].Value, out var months) && months is > 0 and <= 60)
        {
            return new InferredRange(today.AddMonths(-months), today);
        }

        var month = MonthPattern().Match(text);
        if (month.Success)
        {
            var monthNumber = MonthNumber(month.Groups[1].Value);
            if (monthNumber > 0)
            {
                var hasYear = int.TryParse(month.Groups[2].Value, out var explicitYear);

                // No year given: start from the most recent occurrence that has already happened.
                // The caller can step further back if that window holds no bills.
                var year = hasYear
                    ? explicitYear
                    : monthNumber <= today.Month ? today.Year : today.Year - 1;

                var start = new DateOnly(year, monthNumber, 1);
                return new InferredRange(start, start.AddMonths(1).AddDays(-1), YearWasGuessed: !hasYear);
            }
        }

        var year4 = YearPattern().Match(text);
        if (year4.Success && int.TryParse(year4.Groups[1].Value, out var y) && y is >= 2000 and <= 2100)
        {
            return YearRange(y);
        }

        return new InferredRange(null, null);
    }

    private static InferredRange YearRange(int year) =>
        new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    private static int MonthNumber(string name)
    {
        for (var i = 1; i <= 12; i++)
        {
            var full = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(i);
            if (name.Equals(full, StringComparison.OrdinalIgnoreCase)
                || name.Equals(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(i), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"last\s+(\d{1,2})\s+months?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastMonthsPattern();

    [GeneratedRegex(@"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b\s*(\d{4})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"\b(20\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}
