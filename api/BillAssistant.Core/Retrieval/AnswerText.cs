namespace BillAssistant.Core.Retrieval;

/// <summary>
/// Strips the prompt's own scaffolding out of a model answer.
/// </summary>
/// <remarks>
/// llama3.2 echoes the tags it was given: asked to add two bills, three replies in four ended with a
/// literal "&lt;excerpts&gt; [4] &lt;/excerpts&gt;" or began reprinting the totals block verbatim. The
/// reader never saw the prompt, so a tag in the answer is noise at best and a confusing half-quotation
/// at worst.
///
/// Done in code rather than by instruction alone, for the same reason the arithmetic is: a 3B model
/// does not reliably follow an instruction not to, and the guarantee is cheap to make here.
/// </remarks>
public static class AnswerText
{
    /// <summary>Opening fragments of every tag the prompt uses. Matched case-insensitively.</summary>
    private static readonly string[] Tags =
        ["<totals", "</totals", "<excerpts", "</excerpts", "<today", "</today", "<bill", "</bill"];

    /// <summary>Returns the answer up to the point it starts quoting the prompt back.</summary>
    public static string Clean(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var cut = FirstTagIndex(answer, 0);
        return (cut < 0 ? answer : answer[..cut]).Trim();
    }

    private static int FirstTagIndex(string text, int start)
    {
        var earliest = -1;

        foreach (var tag in Tags)
        {
            var i = text.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (earliest < 0 || i < earliest))
            {
                earliest = i;
            }
        }

        return earliest;
    }

    /// <summary>
    /// The streaming counterpart. Tokens arrive one at a time, so a tag can straddle two of them;
    /// this holds back any trailing text that might still grow into one, and emits the rest.
    /// </summary>
    public sealed class Filter
    {
        private string _pending = string.Empty;
        private bool _stopped;

        /// <summary>Text that is safe to show, which may be empty while a possible tag is buffered.</summary>
        public string Push(string token)
        {
            if (_stopped || string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            _pending += token;

            var cut = FirstTagIndex(_pending, 0);
            if (cut >= 0)
            {
                var emit = _pending[..cut];
                _pending = string.Empty;
                _stopped = true;
                return emit;
            }

            // A '<' near the end may be the first character of a tag that has not fully arrived.
            var hold = _pending.LastIndexOf('<');
            if (hold >= 0 && Tags.Any(t => t.StartsWith(_pending[hold..], StringComparison.OrdinalIgnoreCase)))
            {
                var emit = _pending[..hold];
                _pending = _pending[hold..];
                return emit;
            }

            var all = _pending;
            _pending = string.Empty;
            return all;
        }

        /// <summary>Whatever was held back and never turned into a tag.</summary>
        public string Flush()
        {
            if (_stopped)
            {
                return string.Empty;
            }

            var remaining = _pending;
            _pending = string.Empty;
            return remaining;
        }
    }
}
