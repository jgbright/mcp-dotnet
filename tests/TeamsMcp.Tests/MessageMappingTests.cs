using Microsoft.Graph.Models;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// MapMessage() applies the output conventions: omit what is uninteresting, count what is filtered out,
/// never drop anything silently.
/// </summary>
public class MapTests
{
    private static ChatMessage Message(string id = "1", string? html = "<p>hello</p>") => new()
    {
        Id = id,
        CreatedDateTime = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
        MessageType = ChatMessageType.Message,
        From = new ChatMessageFromIdentitySet { User = new Identity { DisplayName = "Mike" } },
        Body = new ItemBody { ContentType = BodyType.Html, Content = html },
    };

    [Fact]
    public void Normal_message_maps_body_sender_and_omits_the_default_message_type()
    {
        var counts = new TeamsTools.SkipCounter();

        var dto = TeamsTools.MapMessage(Message(), includeSystem: false, bodyLimit: 2000, includeReplies: false, counts);

        Assert.NotNull(dto);
        Assert.Equal("1", dto.Id);
        Assert.Equal("Mike", dto.Sender);
        Assert.Equal("hello", dto.Body);
        Assert.Null(dto.MessageType); // "Message" is the default, so it is omitted
        Assert.Null(dto.Truncated);
        Assert.Null(dto.Edited);
        Assert.Null(dto.Attachments);
        Assert.Null(dto.Reactions);
        Assert.Null(dto.Replies);
        Assert.Null(counts.ToDto());
    }

    [Fact]
    public void Deleted_message_is_skipped_and_counted()
    {
        var counts = new TeamsTools.SkipCounter();
        var msg = Message();
        msg.DeletedDateTime = DateTimeOffset.UtcNow;

        Assert.Null(TeamsTools.MapMessage(msg, includeSystem: true, 2000, includeReplies: false, counts));
        Assert.Equal(1, counts.Deleted);
        Assert.Equal(0, counts.System);
    }

    [Fact]
    public void System_message_is_skipped_and_counted_by_default()
    {
        var counts = new TeamsTools.SkipCounter();
        var msg = Message();
        msg.MessageType = ChatMessageType.SystemEventMessage;

        Assert.Null(TeamsTools.MapMessage(msg, includeSystem: false, 2000, includeReplies: false, counts));
        Assert.Equal(1, counts.System);
        Assert.Equal(new SkippedDto(null, 1), counts.ToDto());
    }

    [Fact]
    public void System_message_is_kept_and_labelled_when_requested()
    {
        var counts = new TeamsTools.SkipCounter();
        var msg = Message();
        msg.MessageType = ChatMessageType.SystemEventMessage;

        var dto = TeamsTools.MapMessage(msg, includeSystem: true, 2000, includeReplies: false, counts);

        Assert.Equal("SystemEventMessage", dto?.MessageType);
        Assert.Null(counts.ToDto());
    }

    [Fact]
    public void Deletion_wins_over_include_system()
    {
        var counts = new TeamsTools.SkipCounter();
        var msg = Message();
        msg.MessageType = ChatMessageType.SystemEventMessage;
        msg.DeletedDateTime = DateTimeOffset.UtcNow;

        Assert.Null(TeamsTools.MapMessage(msg, includeSystem: true, 2000, includeReplies: false, counts));
        Assert.Equal(1, counts.Deleted);
        Assert.Equal(0, counts.System);
    }

    [Fact]
    public void Application_sender_is_used_when_there_is_no_user()
    {
        var msg = Message();
        msg.From = new ChatMessageFromIdentitySet { Application = new Identity { DisplayName = "Azure Pipelines" } };

        var dto = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter());

        Assert.Equal("Azure Pipelines", dto?.Sender);
    }

    [Fact]
    public void Missing_sender_and_body_stay_null_rather_than_empty()
    {
        var msg = new ChatMessage { Id = "1", MessageType = ChatMessageType.Message };

        var dto = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter());

        Assert.NotNull(dto);
        Assert.Null(dto.Sender);
        Assert.Null(dto.Body);
    }

    [Fact]
    public void Edited_is_true_only_when_the_message_was_edited()
    {
        var msg = Message();
        Assert.Null(TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter())?.Edited);

        msg.LastEditedDateTime = DateTimeOffset.UtcNow;
        Assert.True(TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter())?.Edited);
    }

    [Fact]
    public void Body_is_truncated_at_the_limit_and_flagged()
    {
        var msg = Message(html: new string('x', 50));

        var dto = TeamsTools.MapMessage(msg, false, bodyLimit: 10, includeReplies: false, new TeamsTools.SkipCounter());

        Assert.Equal(new string('x', 10), dto?.Body);
        Assert.True(dto?.Truncated);
    }

    [Fact]
    public void Attachments_without_a_name_or_content_type_are_dropped()
    {
        var msg = Message();
        msg.Attachments =
        [
            new ChatMessageAttachment { Name = "spec.docx", ContentType = "reference" },
            new ChatMessageAttachment { Name = null, ContentType = null },
        ];

        var dto = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter());

        Assert.Equal(new AttachmentDto("spec.docx", "reference"), Assert.Single(dto!.Attachments!));
    }

    [Fact]
    public void Reactions_are_grouped_by_emoji_and_attributed_to_who_reacted()
    {
        var msg = Message();
        msg.Reactions =
        [
            Reaction("👍", "Alice"),
            Reaction("👍", "Jason Bright"),
            Reaction("✅", "Mike"),
            new ChatMessageReaction { ReactionType = null },
        ];

        var reactions = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter())?.Reactions;

        Assert.Equal(new Dictionary<string, List<string>>
        {
            ["👍"] = ["Alice", "Jason Bright"],
            ["✅"] = ["Mike"],
        }, reactions);
    }

    [Fact]
    public void Reactor_without_a_display_name_falls_back_to_id_then_to_a_placeholder()
    {
        // Graph routinely returns reaction identities with only an id; the list's length must
        // still be the reaction's count, so nobody is dropped.
        var msg = Message();
        msg.Reactions =
        [
            new ChatMessageReaction
            {
                ReactionType = "👍",
                User = new ChatMessageReactionIdentitySet { User = new Identity { Id = "guid-1" } },
            },
            new ChatMessageReaction { ReactionType = "👍" },
        ];

        var reactions = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter())?.Reactions;

        Assert.Equal(["guid-1", "?"], reactions?["👍"]);
    }

    [Fact]
    public void Custom_reaction_is_keyed_by_its_name_rather_than_the_literal_custom()
    {
        var msg = Message();
        var custom = Reaction("custom", "Alice");
        custom.DisplayName = "party parrot";
        msg.Reactions = [custom];

        var reactions = TeamsTools.MapMessage(msg, false, 2000, false, new TeamsTools.SkipCounter())?.Reactions;

        Assert.Equal(["Alice"], reactions?["party parrot"]);
    }

    private static ChatMessageReaction Reaction(string type, string who) => new()
    {
        ReactionType = type,
        User = new ChatMessageReactionIdentitySet { User = new Identity { DisplayName = who } },
    };

    [Fact]
    public void Replies_are_nested_oldest_first_only_when_requested()
    {
        var msg = Message();
        msg.Replies =
        [
            Reply("b", 2),
            Reply("a", 1),
        ];

        Assert.Null(TeamsTools.MapMessage(msg, false, 2000, includeReplies: false, new TeamsTools.SkipCounter())?.Replies);

        var replies = TeamsTools.MapMessage(msg, false, 2000, includeReplies: true, new TeamsTools.SkipCounter())?.Replies;

        Assert.Equal(new[] { "a", "b" }, replies!.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Skipped_replies_are_counted_in_the_same_envelope_and_leave_no_empty_list()
    {
        var counts = new TeamsTools.SkipCounter();
        var deleted = Reply("a", 1);
        deleted.DeletedDateTime = DateTimeOffset.UtcNow;
        var msg = Message();
        msg.Replies = [deleted];

        var dto = TeamsTools.MapMessage(msg, false, 2000, includeReplies: true, counts);

        Assert.Null(dto?.Replies); // an empty replies array would only cost tokens
        Assert.Equal(1, counts.Deleted);
    }

    [Fact]
    public void Replies_are_not_recursed_a_second_level()
    {
        var grandchild = Reply("c", 3);
        var child = Reply("b", 2);
        child.Replies = [grandchild];
        var msg = Message();
        msg.Replies = [child];

        var replies = TeamsTools.MapMessage(msg, false, 2000, includeReplies: true, new TeamsTools.SkipCounter())?.Replies;

        Assert.Null(Assert.Single(replies!).Replies);
    }

    private static ChatMessage Reply(string id, int minute) => new()
    {
        Id = id,
        ReplyToId = "1",
        MessageType = ChatMessageType.Message,
        CreatedDateTime = new DateTimeOffset(2026, 7, 1, 12, minute, 0, TimeSpan.Zero),
        Body = new ItemBody { ContentType = BodyType.Text, Content = id },
    };
}

public class TruncateBodyTests
{
    [Fact]
    public void Null_body_stays_null()
    {
        var (body, truncated) = TeamsTools.TruncateBody(null, 10);

        Assert.Null(body);
        Assert.Null(truncated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_limit_means_unlimited(int limit)
    {
        var (body, truncated) = TeamsTools.TruncateBody("hello", limit);

        Assert.Equal("hello", body);
        Assert.Null(truncated);
    }

    [Fact]
    public void Body_exactly_at_the_limit_is_not_flagged()
    {
        var (body, truncated) = TeamsTools.TruncateBody("hello", 5);

        Assert.Equal("hello", body);
        Assert.Null(truncated);
    }

    [Fact]
    public void Longer_body_is_cut_and_flagged()
    {
        var (body, truncated) = TeamsTools.TruncateBody("hello", 3);

        Assert.Equal("hel", body);
        Assert.True(truncated);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // "ab😀cd" is six chars. The emoji is a surrogate pair at indexes 2 and 3, so a limit of 3
        // lands in the middle of it, and half a pair is an invalid character.
        var (body, truncated) = TeamsTools.TruncateBody("ab😀cd", 3);

        Assert.Equal("ab", body);
        Assert.True(truncated);
    }
}

public class TrimDescriptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_descriptions_are_dropped(string? description)
    {
        Assert.Null(TeamsTools.TrimDescription(description, "Engineering"));
    }

    [Fact]
    public void Description_that_merely_repeats_the_name_is_dropped()
    {
        Assert.Null(TeamsTools.TrimDescription("Engineering", "engineering"));
    }

    [Fact]
    public void Short_description_is_kept_verbatim()
    {
        Assert.Equal("Platform team", TeamsTools.TrimDescription("Platform team", "Engineering"));
    }

    [Fact]
    public void Long_description_is_cut_at_100_characters_with_an_ellipsis()
    {
        var trimmed = TeamsTools.TrimDescription(new string('x', 101), "Engineering");

        Assert.Equal(new string('x', 100) + "…", trimmed);
    }
}

public class ParseSinceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_since_means_no_filter(string? since)
    {
        Assert.Null(TeamsTools.ParseSince(since));
    }

    [Fact]
    public void Utc_timestamp_is_parsed()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), TeamsTools.ParseSince("2026-07-01T00:00:00Z"));
    }

    [Fact]
    public void Offset_is_preserved_rather_than_reinterpreted_as_local()
    {
        var parsed = TeamsTools.ParseSince("2026-07-01T00:00:00-07:00");

        Assert.Equal(TimeSpan.FromHours(-7), parsed?.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero), parsed?.ToUniversalTime());
    }

    [Fact]
    public void Date_only_is_accepted()
    {
        Assert.Equal(new DateTime(2026, 7, 1), TeamsTools.ParseSince("2026-07-01")?.DateTime);
    }

    [Fact]
    public void Garbage_is_rejected_with_a_model_facing_message()
    {
        var e = Assert.Throws<McpException>(() => TeamsTools.ParseSince("last tuesday"));

        Assert.Contains("last tuesday", e.Message);
        Assert.Contains("ISO-8601", e.Message);
    }
}

public class SkipCounterTests
{
    [Fact]
    public void Nothing_skipped_produces_no_envelope_at_all()
    {
        Assert.Null(new TeamsTools.SkipCounter().ToDto());
    }

    [Fact]
    public void Only_non_zero_counts_are_reported()
    {
        var counts = new TeamsTools.SkipCounter { Deleted = 2 };

        Assert.Equal(new SkippedDto(2, null), counts.ToDto());
    }

    [Fact]
    public void Both_counts_are_reported_when_both_fired()
    {
        var counts = new TeamsTools.SkipCounter { Deleted = 2, System = 3 };

        Assert.Equal(new SkippedDto(2, 3), counts.ToDto());
    }
}

public class DescribeResultTests
{
    [Fact]
    public void Message_results_are_summarized_by_count_not_content()
    {
        var result = new MessagesResult(
            [new MessageDto("1", null, null, null, "Mike", "secret content", null, null, null, null, null)],
            true,
            new SkippedDto(1, null));

        var described = TeamsTools.Describe(result);

        Assert.Equal(" messages=1 hasMore=true skipped.deleted=1 skipped.system=0", described);
        Assert.DoesNotContain("secret", described);
    }

    [Fact]
    public void Message_results_omit_has_more_when_there_is_none()
    {
        Assert.Equal(" messages=0", TeamsTools.Describe(new MessagesResult([], null, null)));
    }

    [Fact]
    public void Sent_message_is_summarized_by_id()
    {
        Assert.Equal(
            " messageId=\"17\"",
            TeamsTools.Describe(new SentMessageDto("17", DateTimeOffset.UtcNow, "https://teams/x")));
    }

    [Fact]
    public void Reaction_is_summarized_by_message_and_emoji()
    {
        Assert.Equal(
            " messageId=\"17\" reaction=\"🤔\"",
            TeamsTools.Describe(new ReactionDto("17", "🤔", null)));
        Assert.Equal(
            " messageId=\"17\" reaction=\"🤔\" removed=true",
            TeamsTools.Describe(new ReactionDto("17", "🤔", true)));
    }

    [Fact]
    public void Collections_are_summarized_by_count()
    {
        Assert.Equal(" count=2", TeamsTools.Describe(new List<TeamDto> { new("a", "A", null), new("b", "B", null) }));
    }

    [Fact]
    public void Anything_else_is_summarized_as_nothing()
    {
        Assert.Equal("", TeamsTools.Describe(null));
        Assert.Equal("", TeamsTools.Describe(42));
    }
}
