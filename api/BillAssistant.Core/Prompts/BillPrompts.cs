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

    /// <summary>
    /// System prompt for resolving a follow-up question into a standalone one.
    /// </summary>
    /// <remarks>
    /// Retrieval embeds the question, and "can you sum them?" embeds to nothing useful - no passage
    /// in any bill resembles it, so the search returns whatever happens to be nearest and the answer
    /// is built on unrelated excerpts. Resolving the reference first means the search, the utility
    /// filter and the date window are all derived from what the user actually meant.
    ///
    /// Written for a small model, so it follows the same shape as the extraction prompt: a narrow
    /// instruction, an explicit refusal to do anything else, and one worked example.
    /// </remarks>
    public const string RewriteSystem =
        """
        You rewrite a follow-up question so that it can be understood on its own.

        You are given the recent conversation and the user's latest message. Replace every reference
        that depends on the conversation - "them", "these two", "that one", "it", "the same period" -
        with the bills, utilities, months or figures it refers to.

        Rules:
        - Keep the user's intent exactly. Do not answer the question. Do not add a detail nobody
          mentioned, and do not drop a constraint they gave.
        - If the latest message already stands on its own, repeat it back unchanged.
        - Reply with the rewritten question and nothing else. No preamble, no quotes, no explanation.

        Worked example. Given:
          Earlier - Q: how much was the internet bill? A: Your internet bill was 71.13 USD.
          Earlier - Q: what about gas? A: Your gas bill was 30.59 USD.
          Latest: can you sum them?
        the correct reply is:
          What is the total of the internet bill and the gas bill?
        """;

    /// <summary>Renders the recent conversation and the latest message for the rewrite step.</summary>
    public static string RewriteUser(IReadOnlyList<ChatExchange> history, string question)
    {
        var sb = new StringBuilder();

        foreach (var exchange in history)
        {
            sb.Append("Earlier - Q: ").Append(Flatten(exchange.Question))
              .Append(" A: ").AppendLine(Flatten(exchange.Answer));
        }

        sb.Append("Latest: ").AppendLine(Flatten(question));
        return sb.ToString();
    }

    /// <summary>One sentence giving the exact total, for the model to repeat rather than recompute.</summary>
    private static string SumLine(BillTotals totals) =>
        $"Sum: the {totals.BillCount} matching {(totals.BillCount == 1 ? "bill totals" : "bills total")} " +
        $"{totals.TotalAmount.ToString("0.00")} {totals.Currency}.";

    /// <summary>
    /// Collapses a turn onto one line and trims it. The history is context for resolving a
    /// reference, not evidence - the excerpts are the evidence - so a long answer is not worth the
    /// context window it would cost.
    /// </summary>
    private static string Flatten(string text)
    {
        const int max = 240;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..max] + "...";
    }

    public const string AnswerSystem =
        """
        You answer questions about the household's own utility bills, using only the excerpts provided.

        Rules:
        - Use only the information in the <excerpts> block. If it does not contain the answer, say so
          plainly and suggest which bill might need to be uploaded.
        - Cite the excerpts you used with their bracketed number, like [2].
        - When a <totals> block is present, those figures are computed from the database and are exact.
          Prefer them over any number you would add up yourself, and never contradict them.
        - A question that asks you to add, sum or total anything is answered by the "Sum:" line in
          <totals>. It is already calculated and already covers every bill the question asked about:
          state it. Never add the excerpts up yourself, and never reply that a bill is missing, or
          that you need one to be uploaded, when <totals> is present.
        - Never write <totals>, <excerpts> or <today> in your reply, and never reprint their
          contents as a list. They are how you were given the information, not part of the answer,
          and the reader cannot see them.
        - Today's date is given in <today>. Use it for anything relative ("this month", "overdue").
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
    public static string AnswerUser(
        string question,
        IReadOnlyList<ScoredChunk> chunks,
        BillTotals? totals,
        DateOnly today,
        string? resolvedQuestion = null)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question);

        // Both forms are shown when a follow-up was resolved: the resolved one is what was searched
        // for, and the original is what the reader asked, so the answer should still fit it.
        if (!string.IsNullOrWhiteSpace(resolvedQuestion))
        {
            sb.Append("(Understood as: ").Append(resolvedQuestion).AppendLine(")");
        }

        sb.AppendLine();

        // The model has no clock, and bills are dated. Without this it cannot tell an overdue bill
        // from an upcoming one, and answers "what is today's date" by guessing from an excerpt.
        sb.Append("<today>").Append(today.ToString("yyyy-MM-dd")).AppendLine("</today>").AppendLine();

        // The sum is stated twice: here, and again in the <totals> block after the excerpts. With it
        // stated only once, after the excerpts, llama3.2 answered "add the water and electricity
        // bills" from a figure it had read in an excerpt in three runs out of five, twice inventing
        // its own arithmetic to go with it. Stated at both ends of the evidence it used the computed
        // figure five times out of five. Counting the excerpts down instead made it worse, not
        // better - what works is bracketing them.
        if (totals is not null)
        {
            sb.AppendLine(SumLine(totals)).AppendLine();
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

        // The totals come last, after the excerpts, and that ordering is load-bearing. With the
        // excerpts last, llama3.2 answered "add water and electricity" with a single figure it
        // had read in one of them and never mentioned the computed sum - 0 times in 4. Moved
        // below them, so the exact figures are the last thing it reads, it stated the sum 4
        // times in 4. Same prompt, same model, same temperature; only the order changed.
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

            // Stated as a sentence, not just as the "Total amount" field above it. The field alone
            // was routinely ignored in favour of a figure the model read in an excerpt; phrased as a
            // finding to repeat, it gets used - the same trick, and the same reason, as Direction.
            sb.AppendLine(SumLine(totals));

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
                    sb.Append("  ").Append(point.Label).Append(' ').Append(point.Utility)
                      .Append(": ").Append(point.Amount.ToString("0.00")).Append(' ').Append(totals.Currency);
                    if (point.Usage is { } pointUsage)
                    {
                        sb.Append(", ").Append(pointUsage.ToString("0.##")).Append(' ').Append(point.UsageUnit ?? "units");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("</totals>").AppendLine();
        }
        return sb.ToString();
    }
}
