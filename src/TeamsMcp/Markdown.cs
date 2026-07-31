using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMcp;

/// <summary>
/// Markdown to Teams HTML, for the send tools' <c>format: "markdown"</c>. A model writes markdown
/// far more reliably than hand-balanced HTML, so the conversion happens server-side, targeting
/// only the subset Teams chat actually renders: paragraphs, headings, bold/italic, links, inline
/// code and fenced blocks, lists, blockquotes and rules. Text content is HTML-escaped first, so
/// markup in the input arrives as literal characters rather than being interpreted — the only
/// tags in the output are the ones this converter emits.
/// </summary>
internal static partial class Markdown
{
    private enum Block { Paragraph, Heading, Other }

    internal static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<(Block Kind, string Html)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (FenceRegex().IsMatch(line))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !FenceRegex().IsMatch(lines[i]))
                {
                    code.Add(lines[i]);
                    i++;
                }
                blocks.Add((Block.Other, $"<pre>{Escape(string.Join("\n", code))}</pre>"));
                continue;
            }

            // A rule can look like a bullet ("- - -"), so it is tested first.
            if (RuleRegex().IsMatch(line))
            {
                blocks.Add((Block.Other, "<hr/>"));
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var text = Inline(heading.Groups[2].Value.Trim());
                // Teams has no styling for h4-h6, so deep levels fall back to a bold paragraph.
                blocks.Add(level <= 3
                    ? (Block.Heading, $"<h{level}>{text}</h{level}>")
                    : (Block.Heading, $"<p><b>{text}</b></p>"));
                continue;
            }

            if (QuoteRegex().IsMatch(line))
            {
                var quoted = new List<string>();
                while (i < lines.Length && QuoteRegex().IsMatch(lines[i]))
                {
                    quoted.Add(Inline(QuoteRegex().Match(lines[i]).Groups[1].Value));
                    i++;
                }
                i--;
                blocks.Add((Block.Other, $"<blockquote>{string.Join("<br/>", quoted)}</blockquote>"));
                continue;
            }

            if (BulletRegex().IsMatch(line) || OrderedRegex().IsMatch(line))
            {
                var ordered = OrderedRegex().IsMatch(line);
                var marker = ordered ? OrderedRegex() : BulletRegex();
                var items = new List<string>();
                while (i < lines.Length && marker.IsMatch(lines[i]))
                {
                    items.Add(Inline(marker.Match(lines[i]).Groups[1].Value.Trim()));
                    i++;
                }
                i--;
                var tag = ordered ? "ol" : "ul";
                blocks.Add((Block.Other,
                    $"<{tag}>{string.Concat(items.Select(item => $"<li>{item}</li>"))}</{tag}>"));
                continue;
            }

            // Paragraph: consecutive plain lines, single newlines becoming line breaks.
            var paragraph = new List<string> { Inline(line.Trim()) };
            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]) && !IsBlockStart(lines[i + 1]))
            {
                i++;
                paragraph.Add(Inline(lines[i].Trim()));
            }
            blocks.Add((Block.Paragraph, string.Join("<br/>", paragraph)));
        }
        return Assemble(blocks);
    }

    /// <summary>
    /// Spacing is explicit because Teams chat renders <c>&lt;p&gt;</c> with no margin (measured in
    /// the web client — adjacent paragraphs touch). Consecutive paragraphs therefore merge into one
    /// <c>&lt;p&gt;</c> separated by a double <c>&lt;br/&gt;</c>: a literal blank line on every
    /// client, independent of CSS. A heading gets an <c>&amp;nbsp;</c> spacer paragraph above it —
    /// the same idiom the Teams composer emits for a blank line — except at the start or under
    /// another heading. Lists, code blocks and quotes carry their own margins and get nothing.
    /// </summary>
    private static string Assemble(List<(Block Kind, string Html)> blocks)
    {
        var html = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Kind == Block.Paragraph)
            {
                var parts = new List<string> { blocks[i].Html };
                while (i + 1 < blocks.Count && blocks[i + 1].Kind == Block.Paragraph)
                {
                    i++;
                    parts.Add(blocks[i].Html);
                }
                html.Append("<p>").Append(string.Join("<br/><br/>", parts)).Append("</p>");
                continue;
            }

            if (blocks[i].Kind == Block.Heading && i > 0 && blocks[i - 1].Kind != Block.Heading)
            {
                html.Append("<p>&nbsp;</p>");
            }
            html.Append(blocks[i].Html);
        }
        return html.ToString();
    }

    private static bool IsBlockStart(string line) =>
        FenceRegex().IsMatch(line) || RuleRegex().IsMatch(line) || HeadingRegex().IsMatch(line)
        || QuoteRegex().IsMatch(line) || BulletRegex().IsMatch(line) || OrderedRegex().IsMatch(line);

    /// <summary>
    /// Inline markup on one already-block-parsed piece of text. Code spans, links and bare URLs
    /// are converted first and stashed as placeholder tokens, so the emphasis passes cannot reach
    /// into a URL (underscores are routine there) or a code span; the ADO-style word-boundary
    /// emphasis regexes then keep identifiers like <c>body_limit</c> intact in prose too.
    /// </summary>
    private static string Inline(string text)
    {
        text = Escape(text);
        var stash = new List<string>();
        string Stash(string s)
        {
            stash.Add(s);
            return $"{stash.Count - 1}";
        }

        text = CodeSpanRegex().Replace(text, m => Stash($"<code>{m.Groups[1].Value}</code>"));
        text = MdLinkRegex().Replace(text, m =>
            Stash($"<a href=\"{m.Groups[2].Value}\">{Emphasis(m.Groups[1].Value.Trim())}</a>"));
        text = BareUrlRegex().Replace(text, m =>
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            return Stash($"<a href=\"{url}\">{url}</a>") + m.Value[url.Length..];
        });
        text = Emphasis(text);
        return TokenRegex().Replace(text, m => stash[int.Parse(m.Groups[1].Value)]);
    }

    private static string Emphasis(string text)
    {
        text = StrongRegex().Replace(text, "<b>$1</b>");
        text = StrongUnderscoreRegex().Replace(text, "<b>$1</b>");
        text = ItalicRegex().Replace(text, "<i>$1</i>");
        text = ItalicUnderscoreRegex().Replace(text, "<i>$1</i>");
        return text;
    }

    /// <summary>Also escapes quotes, so an escaped URL is safe inside an href attribute.</summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    [GeneratedRegex(@"^[ \t]*(?:```|~~~)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^[ \t]*(#{1,6})[ \t]+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[ \t]*(?:[-*_][ \t]*){3,}$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^[ \t]*>[ \t]?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^[ \t]*\d{1,9}[.)][ \t]+(.*)$")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)(?:[ \t]+&quot;[^&]*&quot;)?\)")]
    private static partial Regex MdLinkRegex();

    [GeneratedRegex(@"https?://[^\s]+")]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?<!\w)\*\*(?=\S)(.+?)(?<=\S)\*\*(?!\w)", RegexOptions.Singleline)]
    private static partial Regex StrongRegex();

    [GeneratedRegex(@"(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)", RegexOptions.Singleline)]
    private static partial Regex StrongUnderscoreRegex();

    [GeneratedRegex(@"(?<!\w)\*(?=\S)([^*\n]+?)(?<=\S)\*(?!\w)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!\w)_(?=\S)([^_\n]+?)(?<=\S)_(?!\w)")]
    private static partial Regex ItalicUnderscoreRegex();
}
