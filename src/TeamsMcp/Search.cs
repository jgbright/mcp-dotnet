using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using ModelContextProtocol;

namespace TeamsMcp;

/// <summary>
/// Builds the KQL the Microsoft Search tools ask with, and reads a hit back out of what the SDK
/// hands over.
///
/// A search hit is not a <see cref="ChatMessage"/>. Graph answers with
/// <c>"@odata.type": "microsoft.graph.chatMessage"</c>, without the leading <c>#</c> the generated
/// discriminator expects, so the SDK falls back to base <see cref="Entity"/> and every chatMessage
/// property lands in <see cref="Entity.AdditionalData"/> as untyped nodes. Everything here reads
/// that bag instead of casting, and tolerates a value arriving as an untyped node, a boxed
/// primitive, or a raw JSON element. A mapper written against the typed model compiles, runs, and
/// returns nothing but nulls.
/// </summary>
internal static partial class Search
{
    /// <summary>
    /// Composes the query string. KQL ANDs a bare sequence of terms, so the parts are juxtaposed.
    ///
    /// <paramref name="since"/> and <paramref name="until"/> become one <c>sent</c> scope so the
    /// service does the narrowing, and it has to be one: the index ignores two <c>sent</c> terms in
    /// a query together and answers as if neither were there. The terms are day-granular.
    /// <c>sent&gt;</c> and <c>sent&lt;</c> exclude the day they name, so each is widened by a day;
    /// the range form <c>sent:a..b</c> includes both days. <see cref="IsInRange"/> applies the exact
    /// timestamps client-side.
    /// </summary>
    internal static string Build(
        string? query, DateTimeOffset? since, bool mentionsOnly, DateTimeOffset? until = null)
    {
        static string Day(DateTimeOffset ts, int offset) =>
            ts.UtcDateTime.Date.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // until is exclusive, so a bound at midnight ends the day before: asking the index for that
        // whole day would spend the page budget on hits the window then drops.
        static DateTimeOffset LastDay(DateTimeOffset until) =>
            until.UtcDateTime.TimeOfDay == TimeSpan.Zero ? until.AddDays(-1) : until;

        var terms = new List<string>();
        if (mentionsOnly)
        {
            terms.Add("IsMentioned:true");
        }
        terms.Add((since, until) switch
        {
            ({ } s, { } u) => $"sent:{Day(s, 0)}..{Day(LastDay(u), 0)}",
            ({ } s, null) => $"sent>{Day(s, -1)}",
            (null, { } u) => $"sent<{Day(LastDay(u), 1)}",
            _ => "",
        });
        if (!string.IsNullOrWhiteSpace(query))
        {
            terms.Add(query.Trim());
        }
        return string.Join(" ", terms.Where(t => t.Length > 0));
    }

    /// <summary>
    /// Refuses a <c>sent</c> term in the caller's query that would silently cancel the date bound:
    /// one alongside <c>since</c>/<c>until</c>, which add their own, or two of them in the query.
    /// Either way the index drops every <c>sent</c> term and the answer looks normal. A single term
    /// on its own works and is left alone. Quoted phrases are not terms, so they are not counted.
    /// </summary>
    internal static void CheckDateTerms(string? query, bool bounded)
    {
        var terms = query is null ? 0 : SentTerm().Count(QuotedPhrase().Replace(query, ""));
        if (terms > 0 && bounded)
        {
            throw new McpException(
                "`query` has a sent term, and this call already bounds the search by date (since/until, " +
                "or the moment a wait starts from); the search index ignores two date terms together. " +
                "Drop the sent term, or on a search pass it alone without since/until.");
        }
        if (terms > 1)
        {
            throw new McpException(
                "`query` has more than one sent term, and the search index ignores them all when there " +
                "are two. Use the range form, sent:2026-07-27..2026-07-31, or the since/until arguments.");
        }
    }

    [GeneratedRegex(@"\bsent\s*(>=|<=|>|<|:|=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SentTerm();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedPhrase();

    /// <summary>
    /// Whether a hit falls in the caller's window: at or after <c>since</c>, before <c>until</c>.
    /// A hit with an unreadable timestamp is kept when no bound was asked for and dropped when one
    /// was, so a waiter never reports an arrival it has no evidence for.
    /// </summary>
    internal static bool IsInRange(SearchHitDto hit, DateTimeOffset? since, DateTimeOffset? until = null) =>
        (since, until) is (null, null) ||
        (hit.Created is { } created && !(created < since) && !(created >= until));

    /// <summary>
    /// Maps one hit to the output DTO. The summary is the only text a hit carries, since Graph
    /// serves no body for chatMessage with or without an explicit <c>fields</c> list, so it is the
    /// content and <c>body_limit</c> truncates it.
    /// </summary>
    internal static SearchHitDto MapHit(SearchHit hit, int bodyLimit)
    {
        var bag = hit.Resource?.AdditionalData;
        var channel = Object(bag, "channelIdentity");
        var teamId = String(Child(channel, "teamId"));
        var channelId = teamId is null ? null : String(Child(channel, "channelId"));

        // A channel hit repeats its channel id as chatId, and a 1:1 chat hit carries a
        // channelIdentity naming the personal-chat substrate, so only the address a follow-up read
        // would open is kept.
        var chatId = teamId is null ? String(Value(bag, "chatId")) : null;

        var (summary, truncated) = TeamsTools.TruncateBody(TeamsTools.FromHtml(hit.Summary), bodyLimit);

        return new SearchHitDto(
            hit.Resource?.Id ?? hit.HitId,
            chatId,
            teamId,
            channelId,
            Timestamp(Value(bag, "createdDateTime")),
            Sender(Object(bag, "from")),
            summary,
            truncated,
            String(Value(bag, "webLink")));
    }

    /// <summary>
    /// The sender's display name. Search answers with the Exchange substrate's
    /// <c>from.emailAddress.name</c>, not the <c>identitySet</c> the message APIs return, so both
    /// shapes are read; whichever is present is the same person.
    /// </summary>
    private static string? Sender(IDictionary<string, UntypedNode>? from) =>
        String(Child(Object(Child(from, "emailAddress")), "name"))
        ?? String(Child(Object(Child(from, "user")), "displayName"))
        ?? String(Child(Object(Child(from, "application")), "displayName"));

    private static object? Value(IDictionary<string, object>? bag, string key) =>
        bag is not null && bag.TryGetValue(key, out var value) ? value : null;

    private static IDictionary<string, UntypedNode>? Object(IDictionary<string, object>? bag, string key) =>
        Object(Value(bag, key));

    private static UntypedNode? Child(IDictionary<string, UntypedNode>? node, string key) =>
        node is not null && node.TryGetValue(key, out var value) ? value : null;

    private static IDictionary<string, UntypedNode>? Object(object? value) => value switch
    {
        UntypedObject o => o.GetValue(),
        _ => null,
    };

    private static string? String(object? value) => value switch
    {
        string s => s.Length == 0 ? null : s,
        UntypedString u => String(u.GetValue()),
        JsonElement { ValueKind: JsonValueKind.String } e => String(e.GetString()),
        _ => null,
    };

    private static DateTimeOffset? Timestamp(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        // Kiota boxes this one as a DateTime. Graph sends UTC ("...Z"); an Unspecified kind is that
        // instant with the marker lost, not a local reading of it.
        DateTime dt => dt.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dt),
            DateTimeKind.Local => new DateTimeOffset(dt).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        },
        _ => String(value) is { } text && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null,
    };
}
