using System.Text.RegularExpressions;

namespace AiDiary.Api.Embeddings;

/// <summary>A part of a note body: <c>Text</c> is <c>body[Start..End]</c>.</summary>
public readonly record struct NoteChunk(int Start, int End, string Text);

/// <summary>
/// Splits a note body into parts of at most <see cref="MaxChars"/> characters.
/// While more than that remains, the cut goes at the start of the paragraph
/// holding the 20,000th remaining character, so that paragraph opens the next
/// part. 20,000 characters stays under bge-m3's 8192-token context even for
/// languages that tokenise at under three characters per token.
/// </summary>
public static partial class NoteChunker
{
    public const int MaxChars = 20_000;

    public static List<NoteChunk> Split(string body, int maxChars = MaxChars)
    {
        var chunks = new List<NoteChunk>();
        var start = 0;
        while (start < body.Length)
        {
            var end = body.Length - start <= maxChars ? body.Length : FindCut(body, start, start + maxChars);
            var text = body[start..end].Trim();
            if (text.Length > 0)
            {
                chunks.Add(new NoteChunk(start, end, text));
            }
            start = end;
        }
        return chunks;
    }

    /// <summary>Cut position in (start, limit]; never returns start, so the loop always advances.</summary>
    private static int FindCut(string body, int start, int limit)
    {
        var window = body[start..limit];

        // Start of the paragraph holding the last character of the window.
        var paragraphStart = LastMatchEnd(ParagraphBreak(), window);
        if (paragraphStart > 0)
        {
            return start + paragraphStart;
        }

        // A single paragraph longer than the budget: last sentence end, then
        // last whitespace, then a hard cut that doesn't split a surrogate pair.
        var sentenceEnd = LastMatchEnd(SentenceEnd(), window);
        if (sentenceEnd > 0)
        {
            return start + sentenceEnd;
        }
        var space = window.LastIndexOfAny([' ', '\n', '\t']);
        if (space > 0)
        {
            return start + space + 1;
        }
        return char.IsHighSurrogate(body[limit - 1]) ? limit - 1 : limit;
    }

    private static int LastMatchEnd(Regex regex, string text)
    {
        var last = 0;
        foreach (Match match in regex.Matches(text))
        {
            last = match.Index + match.Length;
        }
        return last;
    }

    // A blank line (whitespace-only lines count), plus any further blank lines.
    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"[.!?…][""'”’)\]]*\s+")]
    private static partial Regex SentenceEnd();
}
