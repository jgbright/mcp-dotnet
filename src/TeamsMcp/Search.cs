using System.Globalization;
using System.Text.Json;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace TeamsMcp;

/// <summary>
/// The pure half of the Microsoft Search tools: building the KQL a tool asks with, and reading a
/// hit back out of what the SDK hands over.
///
/// A search hit is not a <see cref="ChatMessage"/> and cannot be treated as one. Graph answers
/// with <c>"@odata.type": "microsoft.graph.chatMessage"</c>, without the leading <c>#</c> the
/// generated discriminator expects, so the SDK falls back to the base <see cref="Entity"/> and
/// every chatMessage property lands in <see cref="Entity.AdditionalData"/> as untyped nodes.
/// Everything here reads that bag instead of casting, and tolerates a value arriving as an
/// untyped node, a boxed primitive, or a raw JSON element. A mapper written against the typed
/// model compiles, runs, and returns nothing but nulls.
/// </summary>
internal static class SearchQueries
{
    /// <summary>
    /// Composes the query string. The parts are ANDed by juxtaposition, which is how KQL reads a
    /// bare sequence of terms.
    ///
    /// <paramref name="since"/> becomes a <c>sent&gt;</c> scope so the service does the narrowing,
    /// but that term is day-granular and excludes the named day: <c>sent&gt;2026-07-28</c> returns
    /// nothing from the 28th. So it is backed off by a day, and <see cref="IsAtOrAfter"/> applies
    /// the exact timestamp client-side. The KQL term is an optimization, not the filter.
    /// </summary>
    internal static string Build(string? query, DateTimeOffset? since, bool mentionsOnly)
    {
        var terms = new List<string>();
        if (mentionsOnly)
        {
            terms.Add("IsMentioned:true");
        }
        if (since is { } ts)
        {
            terms.Add($"sent>{ts.UtcDateTime.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            terms.Add(query.Trim());
        }
        return string.Join(" ", terms);
    }

    /// <summary>
    /// Whether a hit satisfies the caller's <c>since</c>. A hit with an unreadable timestamp is
    /// kept when no filter was asked for and dropped when one was: a waiter that accepted it
    /// would report an arrival it has no evidence for.
    /// </summary>
    internal static bool IsAtOrAfter(SearchHitDto hit, DateTimeOffset? since) =>
        since is not { } ts || (hit.Created is { } created && created >= ts);

    /// <summary>
    /// Maps one hit to the output DTO. The summary is the only text a search hit carries (Graph
    /// serves no body for chatMessage, with or without an explicit <c>fields</c> list), so it is
    /// the content, and <c>body_limit</c> truncates it.
    /// </summary>
    internal static SearchHitDto Map(SearchHit hit, int bodyLimit)
    {
        var bag = hit.Resource?.AdditionalData;
        var channel = Object(bag, "channelIdentity");
        var teamId = String(Child(channel, "teamId"));
        var channelId = teamId is null ? null : String(Child(channel, "channelId"));

        // A channel hit repeats its channel id as chatId, and a 1:1 chat hit carries a
        // channelIdentity naming the personal-chat substrate. Only the address a follow-up read
        // would open is returned.
        var chatId = teamId is null ? String(Value(bag, "chatId")) : null;

        var (summary, truncated) = TeamsTools.TruncateBody(TeamsTools.StripHtml(hit.Summary), bodyLimit);

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
    /// <c>from.emailAddress.name</c> rather than the <c>identitySet</c> the message APIs return.
    /// Both shapes are read, and whichever is present is the same person.
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
