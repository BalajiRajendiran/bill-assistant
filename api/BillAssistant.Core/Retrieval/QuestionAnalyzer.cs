using System.Globalization;
using System.Text.RegularExpressions;
using BillAssistant.Core.Models;

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
    IReadOnlyList<UtilityKind> Utilities,
    DateOnly? From,
    DateOnly? To,
    bool YearWasGuessed = false)
{
    /// <summary>
    /// The one utility the question named, or null when it named none - or several.
    /// </summary>
    /// <remarks>
    /// Null has to mean "do not narrow to a single kind" in both cases, but the two are not the same
    /// query: a question naming none covers every bill, while "add the gas and internet bills" covers
    /// exactly two. <see cref="Utilities"/> keeps that distinction, and a total computed from it is
    /// the total that was asked for rather than the total of everything on file.
    /// </remarks>
    public UtilityKind? Utility => Utilities.Count == 1 ? Utilities[0] : null;
}

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
        "total", "totals", "altogether", "combined", "combine", "sum", "overall",

        // Plain arithmetic wording. "Add the gas and internet bills" and "how much was it
        // altogether" are the same request as "total", and asking the model to do the addition is
        // the one thing it reliably gets wrong.
        "add", "adds", "added", "adding", "plus",

        // A bare "how much" / "how many" is a request for a figure. It used to be matched only in
        // its longest forms ("how much did i pay"), so "how much did the gas and water come to"
        // fell through to plain retrieval and the model was left to add the excerpts up itself.
        "how much", "how many",

        "average", "per month on average", "spend", "spent", "spending",
        "trend", "trending", "compare", "comparison",

        // Direction-of-travel wording. People ask "is my water usage going up?" far more often than
        // they say "trend", and that question is answered by comparing figures, not by reading one
        // passage - so it belongs on the SQL path too.
        "going up", "going down", "gone up", "gone down", "rising", "falling",
        "increase", "increasing", "decrease", "decreasing", "higher", "lower",
        "more than last", "expensive", "cheaper"
    ];

    /// <summary>
    /// The aggregate vocabulary, matched on whole words.
    /// </summary>
    /// <remarks>
    /// Whole words matter now that the list contains short ones: a plain substring test reads "add"
    /// out of "address" and "sum" out of "consumption", which would put an ordinary lookup on the
    /// totals path.
    /// </remarks>
    private static readonly Regex AggregatePattern = new(
        $@"\b(?:{string.Join('|', AggregateWords.Select(Regex.Escape))})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Words that name a utility, including the ones that imply it without saying it ("kwh",
    /// "therms"). Grouped per kind so a question can be checked against every kind, not just the
    /// first one that happens to match.
    /// </summary>
    private static readonly (UtilityKind Kind, string[] Words)[] UtilityWords =
    [
        (UtilityKind.Electricity, ["electric", "power", "energy", "kwh", "lighting"]),
        (UtilityKind.Water, ["water", "sewer"]),
        (UtilityKind.Gas, ["gas", "therm", "heating"]),
        (UtilityKind.Internet, ["internet", "broadband", "fibre", "fiber"]),
        (UtilityKind.Waste, ["waste", "trash", "refuse", "recycl"])
    ];

    public static QuestionIntent Analyse(string question, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(question);

        var text = question.ToLowerInvariant();
        var range = InferRange(text, today);

        return new QuestionIntent(
            WantsTotals: AggregatePattern.IsMatch(text),
            Utilities: InferUtilities(text),
            From: range.From,
            To: range.To,
            YearWasGuessed: range.YearWasGuessed);
    }

    /// <summary>
    /// Maps the words a household actually uses onto a utility kind, or null to search every bill.
    /// </summary>
    /// <remarks>
    /// Null means "do not filter", and that covers two cases: a question that names no utility, and
    /// one that names more than one. The second matters. "Add the gas and internet bills" used to
    /// return whichever kind was tested first, silently dropping the other from retrieval, so the
    /// model was asked to compare two bills having been shown only one - and duly reported the other
    /// as missing. A question about two utilities is answered by retrieving both.
    /// </remarks>
    public static UtilityKind? InferUtility(string text)
    {
        var named = InferUtilities(text);
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>
    /// Every utility the question names, in enum order. Empty means it named none.
    /// </summary>
    /// <remarks>
    /// The set, rather than a single kind, is what callers need. "Add the gas and internet bills"
    /// names two: narrowing to either one silently answers a different question, and widening to all
    /// of them totals five bills nobody asked about.
    /// </remarks>
    public static IReadOnlyList<UtilityKind> InferUtilities(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lowered = text.ToLowerInvariant();
        var named = new List<UtilityKind>();

        foreach (var (kind, words) in UtilityWords)
        {
            if (words.Any(w => lowered.Contains(w, StringComparison.Ordinal)))
            {
                named.Add(kind);
            }
        }

        return named;
    }

    /// <summary>
    /// True when a question leans on the conversation around it and cannot be searched for as typed.
    /// </summary>
    /// <remarks>
    /// Resolving a follow-up costs a model call and carries a risk of its own: asked to rewrite a
    /// question that already stands on its own, llama3.2 folds the previous turn into it - "How much
    /// did I pay for electricity in July 2025?" came back as "What is the total of the gas bill and
    /// the electricity bill for July 2025?", which answers something nobody asked. So a question is
    /// only sent for resolution when it actually contains a reference that needs one.
    ///
    /// Deterministic and pure, like the rest of this class: a false negative searches on the question
    /// as typed, which is the behaviour this had before, and a false positive is caught by the length
    /// guard on the rewrite.
    /// </remarks>
    public static bool LooksLikeFollowUp(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return DependentReferencePattern().IsMatch(question);
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

    // A pronoun or a demonstrative standing in for a bill ("sum them", "these two"), or an opening
    // that continues the previous sentence rather than starting a new one ("and water?").
    [GeneratedRegex(
        @"\b(them|they|those|these|that|this|it|its|both|either|neither|same|again|ones?)\b|^\s*(and|also|what about|how about)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DependentReferencePattern();

    [GeneratedRegex(@"last\s+(\d{1,2})\s+months?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastMonthsPattern();

    [GeneratedRegex(@"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b\s*(\d{4})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"\b(20\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}
