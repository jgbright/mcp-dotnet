using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace TeamsMcp.Tests;

/// <summary>
/// The query a search tool asks with. The <c>sent&gt;</c> term is off by a day on purpose, see
/// <see cref="Search.Build"/>.
/// </summary>
public class SearchQueryTests
{
    private static readonly DateTimeOffset Since = new(2026, 7, 28, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Mentions_ask_the_service_to_do_the_filtering()
    {
        Assert.Equal("IsMentioned:true", Search.Build(null, null, mentionsOnly: true));
    }

    [Fact]
    public void Since_becomes_a_day_scope_that_starts_the_day_before()
    {
        // Graph's `sent>2026-07-28` excludes the 28th outright, so asking from 14:30 that day has
        // to ask from the 27th and let IsAtOrAfter trim the morning off.
        Assert.Equal("sent>2026-07-27", Search.Build(null, Since, mentionsOnly: false));
    }

    [Fact]
    public void Since_is_read_in_utc_rather_than_the_callers_offset()
    {
        // 2026-07-28T20:00-07:00 is the 29th in UTC. The scope is built from the instant.
        var evening = new DateTimeOffset(2026, 7, 28, 20, 0, 0, TimeSpan.FromHours(-7));

        Assert.Equal("sent>2026-07-28", Search.Build(null, evening, mentionsOnly: false));
    }

    [Fact]
    public void Terms_are_juxtaposed_which_is_how_kql_ands_them()
    {
        Assert.Equal(
            "IsMentioned:true sent>2026-07-27 from:Alice invoice",
            Search.Build("  from:Alice invoice  ", Since, mentionsOnly: true));
    }

    [Fact]
    public void Nothing_asked_is_an_empty_query_for_the_caller_to_refuse()
    {
        // The service rejects an empty query string with an opaque error, so the tool refuses first
        // with a readable one.
        Assert.Equal("", Search.Build(null, null, mentionsOnly: false));
        Assert.Equal("", Search.Build("   ", null, mentionsOnly: false));
    }

    [Fact]
    public void A_hit_at_the_boundary_counts_as_at_or_after()
    {
        Assert.True(Search.IsAtOrAfter(Hit(Since), Since));
        Assert.True(Search.IsAtOrAfter(Hit(Since.AddSeconds(1)), Since));
        Assert.False(Search.IsAtOrAfter(Hit(Since.AddSeconds(-1)), Since));
    }

    [Fact]
    public void A_hit_with_no_timestamp_is_kept_only_when_nothing_was_asked()
    {
        // A hit with no timestamp cannot be shown to satisfy the filter, and a waiter that took one
        // would report an arrival it has no evidence for.
        Assert.True(Search.IsAtOrAfter(Hit(null), null));
        Assert.False(Search.IsAtOrAfter(Hit(null), Since));
    }

    private static SearchHitDto Hit(DateTimeOffset? created) =>
        new("1", "19:chat", null, null, created, "Alice", "hi", null, null);
}

/// <summary>
/// Reading a hit back. Graph answers a chatMessage search with
/// <c>"@odata.type": "microsoft.graph.chatMessage"</c>, with no leading <c>#</c>, so the SDK hands
/// over a base <see cref="Entity"/> with everything in <c>AdditionalData</c>. These fixtures copy
/// that shape field for field, because a mapper over the typed model returns only nulls.
/// </summary>
public class SearchHitMappingTests
{
    private static UntypedObject Obj(params (string Key, UntypedNode Value)[] fields) =>
        new(fields.ToDictionary(f => f.Key, f => f.Value));

    private static UntypedNode Str(string value) => new UntypedString(value);

    private static SearchHit Hit(
        string summary = "hello <b>there</b>",
        Dictionary<string, object>? resource = null,
        bool withResource = true) => new()
    {
        HitId = "AAMkAD-long-exchange-id=",
        Summary = summary,
        Resource = withResource
            ? new Entity { Id = "1785237731950", AdditionalData = resource ?? ChatResource() }
            : null,
    };

    private static Dictionary<string, object> ChatResource() => new()
    {
        ["chatId"] = "19:42e04e7f7fab49abba7152c7167dba2e@thread.v2",
        ["createdDateTime"] = new DateTime(2026, 7, 28, 11, 22, 13, DateTimeKind.Utc),
        ["webLink"] = "https://teams.microsoft.com/l/message/19%3a42e04/1785237731950",
        ["channelIdentity"] = Obj(),
        ["from"] = Obj(("emailAddress", Obj(("name", Str("Alice")), ("address", Str("alice@example.com"))))),
    };

    private static Dictionary<string, object> ChannelResource() => new()
    {
        // A channel hit repeats the channel id as its chatId.
        ["chatId"] = "19:032a4714d2da4fc1b237e090206b008e@thread.tacv2",
        ["createdDateTime"] = new DateTime(2026, 7, 27, 20, 31, 15, DateTimeKind.Utc),
        ["channelIdentity"] = Obj(
            ("channelId", Str("19:032a4714d2da4fc1b237e090206b008e@thread.tacv2")),
            ("teamId", Str("be65e069-1d66-4884-ae91-cfc2bc206657"))),
        ["from"] = Obj(("emailAddress", Obj(("name", Str("Alice"))))),
    };

    [Fact]
    public void A_chat_hit_carries_the_chat_id_and_nothing_about_channels()
    {
        var dto = Search.MapHit(Hit(), bodyLimit: 1000);

        Assert.Equal("1785237731950", dto.MessageId);
        Assert.Equal("19:42e04e7f7fab49abba7152c7167dba2e@thread.v2", dto.ChatId);
        Assert.Null(dto.TeamId);
        Assert.Null(dto.ChannelId);
        Assert.Equal("Alice", dto.Sender);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 11, 22, 13, TimeSpan.Zero), dto.Created);
        Assert.Equal("hello there", dto.Summary);
        Assert.Null(dto.Truncated);
    }

    [Fact]
    public void A_channel_hit_carries_the_pair_a_channel_read_needs_and_drops_the_duplicate()
    {
        var dto = Search.MapHit(Hit(resource: ChannelResource()), bodyLimit: 1000);

        Assert.Equal("be65e069-1d66-4884-ae91-cfc2bc206657", dto.TeamId);
        Assert.Equal("19:032a4714d2da4fc1b237e090206b008e@thread.tacv2", dto.ChannelId);
        Assert.Null(dto.ChatId); // it only repeated the channel id
    }

    [Fact]
    public void An_empty_channel_identity_does_not_masquerade_as_a_channel()
    {
        // A 1:1 chat hit carries a channelIdentity naming the personal-chat substrate. Without a
        // teamId there is no channel to read, and emitting one would send a follow-up nowhere.
        var resource = ChatResource();
        resource["channelIdentity"] = Obj(("channelId", Str("19:jason_alice@unq.gbl.spaces")));

        var dto = Search.MapHit(Hit(resource: resource), bodyLimit: 1000);

        Assert.Null(dto.ChannelId);
        Assert.Null(dto.TeamId);
        Assert.Equal("19:42e04e7f7fab49abba7152c7167dba2e@thread.v2", dto.ChatId);
    }

    [Fact]
    public void The_identity_set_shape_reads_the_same_as_the_exchange_one()
    {
        // The message APIs answer with from.user.displayName. Search answers with
        // from.emailAddress.name. Both name the same person.
        var resource = ChatResource();
        resource["from"] = Obj(("user", Obj(("displayName", Str("Alice")))));
        Assert.Equal("Alice", Search.MapHit(Hit(resource: resource), 1000).Sender);

        resource["from"] = Obj(("application", Obj(("displayName", Str("Alertmanager")))));
        Assert.Equal("Alertmanager", Search.MapHit(Hit(resource: resource), 1000).Sender);
    }

    [Fact]
    public void The_summary_is_the_content_and_body_limit_truncates_it()
    {
        var dto = Search.MapHit(Hit(summary: new string('x', 50)), bodyLimit: 10);

        Assert.Equal(new string('x', 10), dto.Summary);
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void A_timestamp_that_arrives_without_a_kind_is_read_as_utc()
    {
        // Graph sends "...Z", but the kind can be lost by the time the value is boxed. Reading it
        // as local time would shift every hit by the machine's offset.
        var resource = ChatResource();
        resource["createdDateTime"] = new DateTime(2026, 7, 28, 11, 22, 13, DateTimeKind.Unspecified);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 11, 22, 13, TimeSpan.Zero),
            Search.MapHit(Hit(resource: resource), 1000).Created);
    }

    [Fact]
    public void A_timestamp_that_arrives_as_a_string_is_still_read()
    {
        var resource = ChatResource();
        resource["createdDateTime"] = "2026-07-28T11:22:13Z";

        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 11, 22, 13, TimeSpan.Zero),
            Search.MapHit(Hit(resource: resource), 1000).Created);
    }

    [Fact]
    public void A_hit_stripped_of_everything_maps_to_what_is_left_rather_than_throwing()
    {
        // A service-side shape change lands on this untyped seam first. A hit that lost its
        // resource still has an id and a summary, so those are still returned.
        var dto = Search.MapHit(Hit(withResource: false), bodyLimit: 1000);

        Assert.Equal("AAMkAD-long-exchange-id=", dto.MessageId);
        Assert.Equal("hello there", dto.Summary);
        Assert.Null(dto.ChatId);
        Assert.Null(dto.Sender);
        Assert.Null(dto.Created);
    }
}
