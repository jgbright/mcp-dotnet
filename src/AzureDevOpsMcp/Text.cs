using System.Net;
using System.Text.RegularExpressions;

namespace AzureDevOpsMcp;

/// <summary>
/// Body conversion. Azure DevOps hands back two flavours of authored text and the tools return
/// neither verbatim: work item fields and comments are HTML, pull request descriptions and
/// comments are Markdown. Both are reduced to plain text that keeps what an agent acts on (links
/// as <c>text (url)</c>, images as their alt text, list items as <c>- </c>, table cells separated
/// by <c>|</c>) and drops the rest.
/// </summary>
internal static partial class Text
{
    /// <summary>Truncates after conversion, so the limit counts characters the model will read.</summary>
    internal static (string? Body, bool? Truncated) Truncate(string? body, int limit)
    {
        if (body is null || limit <= 0 || body.Length <= limit)
        {
            return (body, null);
        }
        return (Cut(body, limit), true);
    }

    /// <summary>
    /// Cuts at the limit, stepping back one when that would split a surrogate pair: emoji are
    /// routine here and half of one is an invalid character. Callers guarantee
    /// <c>s.Length &gt; limit &gt;= 1</c>.
    /// </summary>
    internal static string Cut(string s, int limit) =>
        s[..(char.IsHighSurrogate(s[limit - 1]) ? limit - 1 : limit)];

    /// <summary>Null or blank in, null out, so an empty field disappears from the result.</summary>
    internal static string? FromHtml(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Blank(HtmlToText(html));

    internal static string? FromMarkdown(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? null : Blank(MarkdownToText(markdown));

    /// <summary>
    /// Search hits arrive with the matched terms wrapped in &lt;highlighthit&gt; markers and the
    /// surrounding text HTML-encoded. Strip the tags before decoding, so an encoded angle bracket
    /// in the text itself (routine in code, and in work items about code) survives as text
    /// instead of being read as markup.
    /// </summary>
    internal static string? FromHighlight(string? highlight) =>
        string.IsNullOrWhiteSpace(highlight)
            ? null
            : Blank(WebUtility.HtmlDecode(TagRegex().Replace(highlight, ""))
                .Replace('\u00A0', ' ').Trim());

    private static string? Blank(string s) => s.Length == 0 ? null : s;

    /// <summary>How much of an HTML error page is worth quoting. The message is at the top of it.</summary>
    private const int ErrorLimit = 300;

    /// <summary>
    /// The readable part of an HTML error page, or null when the body is not one.
    ///
    /// Azure DevOps answers a rejected or expired credential with a whole page (stylesheet,
    /// scripts, navigation) wrapped around a sentence like "Access Denied: The Personal Access
    /// Token used has expired." Extracting that sentence is what makes an auth failure read as
    /// one line wherever it surfaces. Script and style blocks go first because their contents are
    /// text and would otherwise be the first thing quoted.
    /// </summary>
    internal static string? ErrorFromHtml(string? body)
    {
        if (body is null || body.TrimStart() is not { Length: > 0 } trimmed || trimmed[0] != '<')
        {
            return null;
        }
        var text = HtmlToText(ScriptStyleRegex().Replace(trimmed, " "));
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3).ToList();
        var message = string.Join(" ", lines);
        return message.Length == 0
            ? null
            : message.Length > ErrorLimit ? Cut(message, ErrorLimit) + "…" : message;
    }

    internal static string HtmlToText(string html)
    {
        // An agent acts on links, so keep them as "text (url)".
        var text = AnchorRegex().Replace(html, m =>
        {
            var url = m.Groups[1].Value;
            var inner = TagRegex().Replace(m.Groups[2].Value, "").Trim();
            return inner.Length == 0 || inner == url ? url : $"{inner} ({url})";
        });
        text = ImgRegex().Replace(text, "$1");   // images/emojis survive as alt text
        text = CellRegex().Replace(text, " | "); // </td>, </th>
        text = RowRegex().Replace(text, "\n");   // </tr>
        text = ListItemRegex().Replace(text, "\n- ");
        text = BlockTagRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' '); // strip &nbsp; noise
        return MultiNewlineRegex().Replace(text, "\n\n").Trim();
    }

    internal static string MarkdownToText(string markdown)
    {
        // Autolinks first: <https://…> would otherwise look like a tag to the HTML stripper below.
        var text = AutoLinkRegex().Replace(markdown, "$1");
        // Images before links, since "![alt](url)" contains a "[alt](url)".
        text = MdImageRegex().Replace(text, m => m.Groups[1].Value.Trim());
        text = MdLinkRegex().Replace(text, m =>
        {
            var label = m.Groups[1].Value.Trim();
            var url = m.Groups[2].Value.Trim();
            return label.Length == 0 || label == url ? url : $"{label} ({url})";
        });
        text = FenceRegex().Replace(text, "");        // ``` / ~~~ delimiters, the code itself stays
        text = InlineCodeRegex().Replace(text, "$1"); // `x` -> x
        text = HeadingRegex().Replace(text, "");      // leading #'s, the text stays
        text = RuleRegex().Replace(text, "");         // --- / *** separators
        text = QuoteRegex().Replace(text, "");        // leading > on quoted lines
        text = BulletRegex().Replace(text, "- ");     // *, +, - all normalize to "- "
        // Emphasis markers are stripped only at word boundaries, so identifiers that contain one
        // (body_limit, System.Title_2) survive intact.
        text = StrongRegex().Replace(text, "$1");
        text = StrongUnderscoreRegex().Replace(text, "$1");
        text = ItalicRegex().Replace(text, "$1");
        text = ItalicUnderscoreRegex().Replace(text, "$1");
        // Azure DevOps markdown routinely carries inline HTML (pasted tables, <br>, <img>).
        text = ImgRegex().Replace(text, "$1");
        text = CellRegex().Replace(text, " | ");
        text = RowRegex().Replace(text, "\n");
        text = BlockTagRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        return MultiNewlineRegex().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<a\b[^>]*\bhref=""([^""]*)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<img\b[^>]*\balt=""([^""]*)""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgRegex();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<br ?/?>|</p>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    [GeneratedRegex(@"<(https?://[^>\s]+)>")]
    private static partial Regex AutoLinkRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)\s]*(?:\s+""[^""]*"")?\)")]
    private static partial Regex MdImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]*)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MdLinkRegex();

    [GeneratedRegex(@"^[ \t]*(?:```|~~~).*$", RegexOptions.Multiline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`([^`\n]*)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[ \t]*(?:[-*_][ \t]*){3,}$", RegexOptions.Multiline)]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^[ \t]*>[ \t]?", RegexOptions.Multiline)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"(?<!\w)\*\*(?=\S)(.+?)(?<=\S)\*\*(?!\w)", RegexOptions.Singleline)]
    private static partial Regex StrongRegex();

    [GeneratedRegex(@"(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)", RegexOptions.Singleline)]
    private static partial Regex StrongUnderscoreRegex();

    [GeneratedRegex(@"(?<!\w)\*(?=\S)([^*\n]+?)(?<=\S)\*(?!\w)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!\w)_(?=\S)([^_\n]+?)(?<=\S)_(?!\w)")]
    private static partial Regex ItalicUnderscoreRegex();
}
