using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AzureDevOpsMcp;

/// <summary>
/// Work item references in the HTML a write sends: <c>#1234</c> and <c>AB#1234</c> as text, and
/// anchors to a work item. Azure DevOps links a reference only when a person types it in the web
/// editor, which adds the relation itself in a second request after saving. A REST write gets no
/// relation whatever markup it carries, the editor's own included, so the write tools rewrite each
/// reference to the markup the editor saves and add the relation themselves. Pure, so it is
/// testable without an organization behind it.
/// </summary>
internal static partial class Mentions
{
    /// <summary>The link a reference gets unless the caller names another.</summary>
    internal const string RelatedRel = "System.LinkTypes.Related";

    /// <summary>
    /// The ids referenced across <paramref name="bodies"/>, in order of first appearance. A bare
    /// reference needs three digits or more: "#1" and "#12" are list numbering far more often than
    /// work items, and the web editor itself once linked an item to work item 1 that way.
    /// </summary>
    internal static List<int> Find(params string?[] bodies)
    {
        var ids = new List<int>();
        foreach (var body in bodies)
        {
            if (body is not null)
            {
                Walk(body, id =>
                {
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                    return null;
                });
            }
        }
        return ids;
    }

    /// <summary>
    /// <paramref name="html"/> with every reference to an id in <paramref name="targets"/> (id to
    /// the work item's web url) written as the mention the web editor saves. Other ids stay as
    /// they were, and so does an existing mention or a work item link with text of its own.
    /// </summary>
    internal static string? Rewrite(string? html, IReadOnlyDictionary<int, string> targets) =>
        html is null || targets.Count == 0
            ? html
            : Walk(html, id => targets.TryGetValue(id, out var url) ? Markup(id, url) : null);

    /// <summary>The web editor's mention: trailing slash on the href, and #id as the text.</summary>
    internal static string Markup(int id, string webUrl) =>
        $"<a href=\"{webUrl.TrimEnd('/')}/\" data-vss-mention=\"version:1.0\">#{id}</a>";

    /// <summary>
    /// The ids in <paramref name="referenced"/> that <paramref name="relations"/> does not link to
    /// by any link type, less the ones in <paramref name="exclude"/> (the item itself, a parent this
    /// same write sets).
    /// </summary>
    internal static List<int> Unlinked(
        IEnumerable<int> referenced, IReadOnlyList<WireRelation>? relations, params int?[] exclude)
    {
        var linked = (relations ?? [])
            .Select(r => Mapping.LinkedWorkItemId(r.Url))
            .Concat(exclude)
            .OfType<int>()
            .ToHashSet();
        return referenced.Where(id => !linked.Contains(id)).Distinct().ToList();
    }

    /// <summary>
    /// <c>link_type</c> as a relation name: the names the Links tab shows for the common choices,
    /// or any link type reference name as given.
    /// </summary>
    internal static string Rel(string? linkType)
    {
        if (string.IsNullOrWhiteSpace(linkType))
        {
            return RelatedRel;
        }
        var name = linkType.Trim();
        if (FriendlyRels.TryGetValue(name, out var rel))
        {
            return rel;
        }
        if (name.Contains('.', StringComparison.Ordinal))
        {
            return name;
        }
        throw new ModelContextProtocol.McpException(
            $"`link_type` \"{name}\" is not a link type. Pass one of {string.Join(", ", FriendlyRels.Keys)}, " +
            "or a link type reference name such as System.LinkTypes.Dependency-Reverse.");
    }

    private static readonly Dictionary<string, string> FriendlyRels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Related"] = RelatedRel,
        ["Predecessor"] = "System.LinkTypes.Dependency-Reverse",
        ["Successor"] = "System.LinkTypes.Dependency-Forward",
        ["Duplicate"] = "System.LinkTypes.Duplicate-Forward",
        ["Duplicate Of"] = "System.LinkTypes.Duplicate-Reverse",
    };

    /// <summary>
    /// Visits every reference in <paramref name="html"/>, replacing each with what
    /// <paramref name="replace"/> returns for its id, or leaving it when that is null. Text inside a
    /// tag is never read, so a style attribute's <c>#123456</c> is a color and not a work item. Inside
    /// an anchor, only the anchor counts: a work item link is a reference whatever its text, and a
    /// link anywhere else makes its <c>#123</c> a pull request or an issue, not a work item.
    /// </summary>
    private static string Walk(string html, Func<int, string?> replace)
    {
        var result = new StringBuilder(html.Length);
        var last = 0;
        foreach (Match anchor in AnchorRegex().Matches(html))
        {
            AppendText(result, html[last..anchor.Index], replace);
            result.Append(Anchor(anchor, replace));
            last = anchor.Index + anchor.Length;
        }
        AppendText(result, html[last..], replace);
        return result.ToString();
    }

    private static void AppendText(StringBuilder result, string segment, Func<int, string?> replace)
    {
        var last = 0;
        foreach (Match tag in TagRegex().Matches(segment))
        {
            result.Append(ReplaceRefs(segment[last..tag.Index], replace)).Append(tag.Value);
            last = tag.Index + tag.Length;
        }
        result.Append(ReplaceRefs(segment[last..], replace));
    }

    private static string ReplaceRefs(string text, Func<int, string?> replace) =>
        RefRegex().Replace(text, m => replace(Id(m)) ?? m.Value);

    private static string Anchor(Match anchor, Func<int, string?> replace)
    {
        var attributes = anchor.Groups["attrs"].Value;
        var href = HrefRegex().Match(attributes);
        var target = href.Success ? WorkItemHrefRegex().Match(href.Groups["url"].Value) : Match.Empty;
        if (!target.Success)
        {
            return anchor.Value;
        }
        var id = Id(target);
        // The replacement is asked for even when it will not be used, because asking is also how
        // a reference is reported.
        var mention = replace(id);
        if (attributes.Contains("data-vss-mention", StringComparison.OrdinalIgnoreCase))
        {
            return anchor.Value;
        }
        var text = TagRegex().Replace(anchor.Groups["inner"].Value, "").Trim();
        var isReference = text == $"#{id}" || text == $"AB#{id}";
        return isReference && mention is not null ? mention : anchor.Value;
    }

    private static int Id(Match m) => int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"\bhref\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"/_workitems/edit/(?<id>\d{1,9})/?(?:[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemHrefRegex();

    // Not after a word character, so AB#1234 is read once and x#1234 not at all, and not after &,
    // so a numeric character reference (&#39;) is not an id.
    [GeneratedRegex(@"(?<![\w&])(?:AB)?#(?<id>[1-9]\d{2,8})(?!\w)")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();
}
