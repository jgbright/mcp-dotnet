namespace TeamsMcp.Tests;

/// <summary>
/// A chat can be named rather than only addressed by its Graph id. Two rules carry the risk that
/// comes with that: an ambiguous name is never resolved by recency, and the signed-in user's own
/// name is not a match for every conversation they are in.
/// </summary>
public class ChatAddressingTests
{
    private const string Me = "Jason Bright";

    private static ChatDto Group(string id, string topic, params string[] members) =>
        new(id, topic, "group", null, [.. members]);

    private static ChatDto OneOnOne(string id, params string[] members) =>
        new(id, null, "oneOnOne", null, [.. members]);

    [Fact]
    public void A_group_chat_is_found_by_its_topic()
    {
        var chats = new[]
        {
            Group("19:a@thread.v2", "AA Technology Team", Me, "Mike"),
            OneOnOne("19:b@unq.gbl.spaces", Me, "Libby"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "AA Technology Team", Me);

        Assert.Equal("19:a@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("exact", how);
    }

    [Fact]
    public void A_one_on_one_is_found_by_the_other_person()
    {
        var chats = new[] { OneOnOne("19:b@unq.gbl.spaces", Me, "Libby") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Libby", Me);

        Assert.Equal("19:b@unq.gbl.spaces", Assert.Single(matches).Id);
    }

    [Fact]
    public void An_exact_name_wins_over_a_chat_that_merely_contains_it()
    {
        // Otherwise the shorter, more specific name can never address its own chat.
        var chats = new[]
        {
            Group("19:a@thread.v2", "Stripe", Me, "Mike"),
            Group("19:b@thread.v2", "Stripe Migration", Me, "Libby"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "Stripe", Me);

        Assert.Equal("19:a@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("exact", how);
    }

    [Fact]
    public void A_partial_name_still_matches_when_nothing_is_exact()
    {
        var chats = new[] { Group("19:b@thread.v2", "Stripe Migration", Me, "Libby") };

        var (matches, how) = TeamsTools.MatchChats(chats, "Migration", Me);

        Assert.Equal("19:b@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("substring", how);
    }

    [Fact]
    public void The_signed_in_user_is_not_a_match_for_every_chat_they_are_in()
    {
        // They are a member of all of them, so matching on their own name would make it a wildcard.
        var chats = new[]
        {
            OneOnOne("19:b@unq.gbl.spaces", Me, "Libby"),
            OneOnOne("19:c@unq.gbl.spaces", Me, "Mike"),
        };

        var (matches, _) = TeamsTools.MatchChats(chats, Me, Me);

        Assert.Empty(matches);
    }

    [Fact]
    public void A_name_that_means_two_conversations_returns_both_rather_than_the_recent_one()
    {
        // The listing arrives newest first, so taking the first match would be a silent choice of
        // destination. Both come back and the caller refuses with them named.
        var chats = new[]
        {
            OneOnOne("19:recent@unq.gbl.spaces", Me, "Mike"),
            Group("19:older@thread.v2", "Mike and the release", Me, "Mike", "Libby"),
        };

        var (matches, _) = TeamsTools.MatchChats(chats, "Mike", Me);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Nothing_matching_is_no_match_rather_than_a_guess()
    {
        var chats = new[] { Group("19:a@thread.v2", "AA Technology Team", Me, "Mike") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Billing", Me);

        Assert.Empty(matches);
    }

    [Fact]
    public void Matching_works_before_the_signed_in_name_is_known()
    {
        // GetMeAsync can answer a user with no display name. Resolution still has to work, just
        // without the exclusion.
        var chats = new[] { Group("19:a@thread.v2", "AA Technology Team", Me, "Mike") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Technology", me: null);

        Assert.Single(matches);
    }

    [Fact]
    public void The_self_chat_is_listed_because_graph_never_returns_it()
    {
        var row = TeamsTools.SelfChatRow(Me, member: null, topic: null);

        Assert.NotNull(row);
        Assert.Equal("48:notes", row.Id);
        Assert.Equal("self", row.Kind);
        Assert.Equal(Me, Assert.Single(row.Members));
    }

    [Fact]
    public void A_topic_filter_excludes_the_self_chat_which_has_no_topic()
    {
        Assert.Null(TeamsTools.SelfChatRow(Me, member: null, topic: "Stripe"));
    }

    [Theory]
    [InlineData("Jason", true)]
    [InlineData("bright", true)]
    [InlineData("Libby", false)]
    public void A_member_filter_is_matched_against_the_signed_in_user(string member, bool listed)
    {
        Assert.Equal(listed, TeamsTools.SelfChatRow(Me, member, topic: null) is not null);
    }
}
