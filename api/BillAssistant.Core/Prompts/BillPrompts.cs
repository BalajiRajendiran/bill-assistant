using System.Text;
using BillAssistant.Core.Abstractions;
using BillAssistant.Core.Models;
using BillAssistant.Core.Retrieval;

namespace BillAssistant.Core.Prompts;

/// <summary>Prompt text for the two model-facing steps: metadata extraction and grounded answering.</summary>
public static class BillPrompts
{
    /// <summary>
    /// System prompt for metadata extraction.
    /// </summary>
    /// <remarks>
    /// Written for a small model. Three things were needed to get llama3.2 to fill the schema reliably,
    /// verified against every bill in <c>samples/</c>:
    /// the list of where each fact is printed, an explicit demand for all ten keys (without it the
    /// model returned only the two or three fields it happened to notice), and one worked example
    /// (which also stops it reporting a strapline like "City of Springfield Public Works" as the
    /// provider instead of the actual company name).
    /// </remarks>
    public const string MetadataSystem =
        """
        You extract facts from household utility bills and reply with JSON.

        Fill in EVERY field of the schema. Use null for a field only when the bill genuinely does not
        show it - but a utility bill almost always shows the total due, the service period and the
        usage, so look carefully before giving up on one.

        Where to look:
        - utility: what is being sold. "Electricity statement" means Electricity; water and sewer means Water.
        - providerName: the company that issued the bill, as printed at the top. Not its strapline.
        - amountDue: the line labelled TOTAL AMOUNT DUE or Amount due. Not the previous balance, not a
          payment received, not a per-unit rate.
        - periodStart / periodEnd: the two dates on the "Service period" line.
        - dueDate: the "Payment due date" line.
        - usageQuantity / usageUnit: the "Total usage" line, e.g. 734 kWh -> 734 and "kWh".

        Dates are yyyy-MM-dd. Amounts are plain numbers with no currency symbol or thousands separator.

        Your reply must contain all ten keys, in this order:
        utility, providerName, accountNumber, periodStart, periodEnd, amountDue, currency, dueDate,
        usageQuantity, usageUnit.

        Worked example. For a bill containing:
          Northwind Water  |  Water and sewer statement
          Account number: 5521-9987
          Service period: 2025-03-01 to 2025-03-31
          Payment due date: 2025-04-15
          TOTAL AMOUNT DUE  $61.20
          Total usage  8 CCF
        the correct reply is:
          {"utility":"Water","providerName":"Northwind Water","accountNumber":"5521-9987",
           "periodStart":"2025-03-01","periodEnd":"2025-03-31","amountDue":61.20,"currency":"USD",
           "dueDate":"2025-04-15","usageQuantity":8,"usageUnit":"CCF"}
        """;

    public static string MetadataUser(string billText) =>
        $"""
        Extract the bill details from this text.

        <bill>
        {billText}
        </bill>
        """;

    public const string AnswerSystem =
        """
        You answer questions about the household's own utility bills, using only the excerpts provided.

        Rules:
        - Use only the information in the <excerpts> block. If it does not contain the answer, say so
          plainly and suggest which bill might need to be uploaded.
        - Cite the excerpts you used with their bracketed number, like [2].
        - When a <totals> block is present, those figures are computed from the database and are exact.
          Prefer them over any number you would add up yourself, and never contradict them.
        - For a question about direction - rising, falling, cheaper, worse than last year - the answer is
          the "Direction:" line in <totals>. It is already calculated: state it, with the two figures it
          names. Never judge a trend from the total, which is a sum and says nothing about direction,
          and never say a bill is missing when <totals> lists the periods.
        - Money keeps the currency it was given in. Quote amounts to two decimal places.
        - State figures as your own findings. Write "Your water usage rose 36%, from 14 to 19 CCF."
          Never write "according to the totals block", "the Direction line says" or "the excerpts show"
          - the reader cannot see any of that, so naming it only confuses them.
        - Be brief and direct. Two or three sentences is usually enough.
        """;

    /// <summary>
    /// Builds the grounded user prompt. The excerpts are numbered so the model can cite them, and each
    /// one is labelled with its bill so the model can distinguish July's electricity bill from June's.
    /// </summary>
    public static string AnswerUser(string question, IReadOnlyList<ScoredChunk> chunks, BillTotals? totals)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question).AppendLine();

        if (totals is not null)
        {
            sb.AppendLine("<totals>");
            sb.Append("Bills matched: ").Append(totals.BillCount).AppendLine();
            sb.Append("Total amount: ").Append(totals.TotalAmount.ToString("0.00")).Append(' ').AppendLine(totals.Currency);
            if (totals.TotalUsage is { } usage)
            {
                sb.Append("Total usage: ").Append(usage.ToString("0.##")).Append(' ').AppendLine(totals.UsageUnit ?? "units");
            }

            if (totals.From is { } from && totals.To is { } to)
            {
                sb.Append("Covering: ").Append(from.ToString("yyyy-MM-dd")).Append(" to ").AppendLine(to.ToString("yyyy-MM-dd"));
            }

            // The direction is computed, not left to the model - see TrendSummary.
            if (TrendSummary.Describe(totals.Series, totals.Currency) is { } direction)
            {
                sb.Append("Direction: ").AppendLine(direction);
            }

            // Oldest first, so reading top to bottom is reading the direction of travel.
            if (totals.Series is { Count: > 1 } series)
            {
                sb.AppendLine("By period (oldest first):");
                foreach (var point in series)
                {
                    sb.Append("  ").Append(point.Label).Append(": ").Append(point.Amount.ToString("0.00")).Append(' ').Append(totals.Currency);
                    if (point.Usage is { } pointUsage)
                    {
                        sb.Append(", ").Append(pointUsage.ToString("0.##")).Append(' ').Append(point.UsageUnit ?? "units");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("</totals>").AppendLine();
        }

        sb.AppendLine("<excerpts>");
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i].Chunk;
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(c.Utility).Append(" bill, ")
              .Append(string.IsNullOrWhiteSpace(c.ProviderName) ? "unknown provider" : c.ProviderName)
              .Append(", file ").Append(c.FileName)
              .Append(", page ").Append(c.PageNumber)
              .AppendLine(":");
            sb.AppendLine(c.Text);
            sb.AppendLine();
        }

        sb.AppendLine("</excerpts>");
        return sb.ToString();
    }
}
