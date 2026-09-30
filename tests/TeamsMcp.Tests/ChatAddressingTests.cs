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
            Group("19:a@thread.v2", "Engineering Team", Me, "Bob"),
            OneOnOne("19:b@unq.gbl.spaces", Me, "Carol"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "Engineering Team", Me);

        Assert.Equal("19:a@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("exact", how);
    }

    [Fact]
    public void A_one_on_one_is_found_by_the_other_person()
    {
        var chats = new[] { OneOnOne("19:b@unq.gbl.spaces", Me, "Carol") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Carol", Me);

        Assert.Equal("19:b@unq.gbl.spaces", Assert.Single(matches).Id);
    }

    [Fact]
    public void An_exact_name_wins_over_a_chat_that_merely_contains_it()
    {
        // Otherwise the shorter, more specific name can never address its own chat.
        var chats = new[]
        {
            Group("19:a@thread.v2", "Launch", Me, "Bob"),
            Group("19:b@thread.v2", "Launch Planning", Me, "Carol"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "Launch", Me);

        Assert.Equal("19:a@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("exact", how);
    }

    [Fact]
    public void A_partial_name_still_matches_when_nothing_is_exact()
    {
        var chats = new[] { Group("19:b@thread.v2", "Launch Planning", Me, "Carol") };

        var (matches, how) = TeamsTools.MatchChats(chats, "Planning", Me);

        Assert.Equal("19:b@thread.v2", Assert.Single(matches).Id);
        Assert.Equal("substring", how);
    }

    [Fact]
    public void The_signed_in_user_is_not_a_match_for_every_chat_they_are_in()
    {
        // They are a member of all of them, so matching on their own name would make it a wildcard.
        var chats = new[]
        {
            OneOnOne("19:b@unq.gbl.spaces", Me, "Carol"),
            OneOnOne("19:c@unq.gbl.spaces", Me, "Bob"),
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
            Group("19:recent@thread.v2", "Launch", Me, "Bob Jones", "Carol"),
            Group("19:older@thread.v2", "Bob and the release", Me, "Bob Jones"),
        };

        var (matches, _) = TeamsTools.MatchChats(chats, "Bob Jones", Me);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void A_persons_full_name_means_the_one_on_one_over_the_group_chats_they_are_in()
    {
        // Every name-addressed call in two weeks of logs meant the 1:1, and was refused because
        // the person was also in several group chats.
        var chats = new[]
        {
            Group("19:a@thread.v2", "Everyone", Me, "Alice Chen", "Bob Jones"),
            OneOnOne("19:alice@unq.gbl.spaces", "Alice Chen", Me),
            Group("19:b@thread.v2", "Launch Team", Me, "Alice Chen"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "alice chen", Me);

        Assert.Equal("19:alice@unq.gbl.spaces", Assert.Single(matches).Id);
        Assert.Equal("exact-1:1", how);
    }

    [Fact]
    public void A_topic_equal_to_a_persons_name_still_competes_with_their_one_on_one()
    {
        var chats = new[]
        {
            OneOnOne("19:bob@unq.gbl.spaces", Me, "Bob Jones"),
            Group("19:a@thread.v2", "Bob Jones", Me, "Carol"),
        };

        var (matches, _) = TeamsTools.MatchChats(chats, "Bob Jones", Me);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void A_first_name_alone_is_still_ambiguous_across_a_one_on_one_and_a_group_chat()
    {
        // Guessing which Bob was meant is the risky case, so a substring never reaches the rule.
        var chats = new[]
        {
            OneOnOne("19:bob@unq.gbl.spaces", Me, "Bob Jones"),
            Group("19:a@thread.v2", "Release", Me, "Bob Jones"),
        };

        var (matches, how) = TeamsTools.MatchChats(chats, "Bob", Me);

        Assert.Equal(2, matches.Count);
        Assert.Equal("substring", how);
    }

    [Fact]
    public void Nothing_matching_is_no_match_rather_than_a_guess()
    {
        var chats = new[] { Group("19:a@thread.v2", "Engineering Team", Me, "Bob") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Billing", Me);

        Assert.Empty(matches);
    }

    [Fact]
    public void Matching_works_before_the_signed_in_name_is_known()
    {
        // GetMeAsync can answer a user with no display name. Resolution still has to work, just
        // without the exclusion.
        var chats = new[] { Group("19:a@thread.v2", "Engineering Team", Me, "Bob") };

        var (matches, _) = TeamsTools.MatchChats(chats, "Engineering", me: null);

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
        Assert.Null(TeamsTools.SelfChatRow(Me, member: null, topic: "Launch"));
    }

    [Theory]
    [InlineData("Jason", true)]
    [InlineData("bright", true)]
    [InlineData("Carol", false)]
    public void A_member_filter_is_matched_against_the_signed_in_user(string member, bool listed)
    {
        Assert.Equal(listed, TeamsTools.SelfChatRow(Me, member, topic: null) is not null);
    }
}
