using Microsoft.Graph.Models;

namespace TeamsMcp.Tests;

/// <summary>
/// Paging stops early on purpose: walking all history to answer "anything since T?" is the
/// unbounded scan this design forbids. The hazard is stopping early for the wrong reason.
///
/// Graph orders a message collection by <c>lastModifiedDateTime</c>, not <c>createdDateTime</c>,
/// and a reaction moves the former. Measured 2026-07-31 against the live service: a message
/// created at 09:14:46 sorted above one created at 09:14:48 the moment it was reacted to, and
/// stayed there after the reaction was removed. So an old message can appear at position 0 with
/// nothing new having been said, and a scan that treats "older than the floor" as "end of the
/// newer ones" stops before reaching messages it was asked for.
/// </summary>
public class MessagePagingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 9, 14, 0, TimeSpan.Zero);

    private static ChatMessage Msg(string id, DateTimeOffset created) => new()
    {
        Id = id,
        CreatedDateTime = created,
        MessageType = ChatMessageType.Message,
        From = new ChatMessageFromIdentitySet { User = new Identity { DisplayName = "Jason Bright" } },
        Body = new ItemBody { Content = "hello", ContentType = BodyType.Text },
    };

    /// <summary>One page, no continuation: what a small conversation answers with.</summary>
    private static (TeamsTools.FirstPage First, TeamsTools.NextPage Next) OnePage(params ChatMessage[] messages) =>
        (_ => Task.FromResult<ChatMessageCollectionResponse?>(
             new ChatMessageCollectionResponse { Value = [.. messages] }),
         (_, _) => Task.FromResult<ChatMessageCollectionResponse?>(null));

    private static Task<MessagesResult> Page(
        (TeamsTools.FirstPage, TeamsTools.NextPage) pager, DateTimeOffset floor) =>
        TeamsTools.PageMessagesAsync(
            pager, new Watermark(floor, null), limit: 20,
            includeReplies: false, includeSystem: false, bodyLimit: 2000, CancellationToken.None);

    [Fact]
    public async Task A_reacted_to_old_message_at_the_top_does_not_hide_the_new_one_behind_it()
    {
        // The listing Graph actually returns after someone reacts to a year-old message: it sorts
        // above the genuinely new message, because reacting moved its lastModifiedDateTime.
        var result = await Page(
            OnePage(
                Msg("ancient-but-just-reacted-to", T0.AddYears(-1)),
                Msg("said-a-minute-ago", T0.AddMinutes(1))),
            floor: T0);

        Assert.Equal(["said-a-minute-ago"], result.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Messages_older_than_the_floor_are_still_left_out()
    {
        // The filtering itself must survive the fix: "don't stop early" is not "return everything".
        var result = await Page(
            OnePage(
                Msg("older", T0.AddMinutes(-5)),
                Msg("newer", T0.AddMinutes(5))),
            floor: T0);

        Assert.Equal(["newer"], result.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task A_page_of_nothing_but_old_messages_ends_the_scan()
    {
        // The early stop still has to happen, or a recent floor walks the whole conversation. A
        // second page is offered and must never be fetched.
        var secondPageFetched = false;
        var pager = (
            (TeamsTools.FirstPage)(_ => Task.FromResult<ChatMessageCollectionResponse?>(
                new ChatMessageCollectionResponse
                {
                    Value = [Msg("old-a", T0.AddDays(-2)), Msg("old-b", T0.AddDays(-3))],
                    OdataNextLink = "https://graph.microsoft.com/next",
                })),
            (TeamsTools.NextPage)((_, _) =>
            {
                secondPageFetched = true;
                return Task.FromResult<ChatMessageCollectionResponse?>(null);
            }));

        var result = await Page(pager, floor: T0);

        Assert.Empty(result.Messages);
        Assert.False(secondPageFetched, "a page with nothing at or after the floor ends the scan");
    }

    [Fact]
    public async Task A_page_carrying_one_fresh_message_is_followed()
    {
        // One qualifying message means the next page may hold more, so the scan continues instead
        // of stopping at the first old neighbour.
        var pages = 0;
        var pager = (
            (TeamsTools.FirstPage)(_ =>
            {
                pages++;
                return Task.FromResult<ChatMessageCollectionResponse?>(
                    new ChatMessageCollectionResponse
                    {
                        Value = [Msg("reacted-old", T0.AddYears(-1)), Msg("fresh-1", T0.AddMinutes(2))],
                        OdataNextLink = "https://graph.microsoft.com/next",
                    });
            }),
            (TeamsTools.NextPage)((_, _) =>
            {
                pages++;
                return Task.FromResult<ChatMessageCollectionResponse?>(
                    new ChatMessageCollectionResponse { Value = [Msg("fresh-2", T0.AddMinutes(1))] });
            }));

        var result = await Page(pager, floor: T0);

        Assert.Equal(2, pages);
        Assert.Equal(["fresh-1", "fresh-2"], result.Messages.Select(m => m.Id));
    }

    // ------------------------------------------------------- until

    private static Task<MessagesResult> Window(
        (TeamsTools.FirstPage, TeamsTools.NextPage) pager, DateTimeOffset since, DateTimeOffset until, int limit = 20) =>
        TeamsTools.PageMessagesAsync(
            pager, new Watermark(since, null), limit,
            includeReplies: false, includeSystem: false, bodyLimit: 2000, CancellationToken.None, until);

    [Fact]
    public async Task Messages_at_or_after_until_are_passed_over_without_using_up_the_limit()
    {
        var result = await Window(
            OnePage(
                Msg("next-week", T0.AddDays(8)),
                Msg("exactly-until", T0.AddDays(7)),
                Msg("in-window-late", T0.AddDays(6)),
                Msg("in-window-early", T0.AddDays(1))),
            since: T0, until: T0.AddDays(7), limit: 2);

        Assert.Equal(["in-window-late", "in-window-early"], result.Messages.Select(m => m.Id));
        Assert.Null(result.HasMore);
        Assert.Null(result.Skipped); // a window is the caller's choice, not a filter
    }

    [Fact]
    public async Task A_message_reacted_to_after_the_window_still_appears_in_it()
    {
        // In a channel, Graph lists by lastModifiedDateTime, so a message from the window that
        // somebody reacted to since sorts among the newer ones. It is placed by when it was sent.
        var result = await Window(
            OnePage(
                Msg("in-window-reacted-to-today", T0.AddDays(3)),
                Msg("newer", T0.AddDays(20)),
                Msg("in-window", T0.AddDays(2))),
            since: T0, until: T0.AddDays(7));

        Assert.Equal(["in-window-reacted-to-today", "in-window"], result.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task A_page_of_messages_newer_than_the_window_does_not_end_the_scan()
    {
        var pager = (
            (TeamsTools.FirstPage)(_ => Task.FromResult<ChatMessageCollectionResponse?>(
                new ChatMessageCollectionResponse
                {
                    Value = [Msg("newer-a", T0.AddDays(30)), Msg("newer-b", T0.AddDays(29))],
                    OdataNextLink = "https://graph.microsoft.com/next",
                })),
            (TeamsTools.NextPage)((_, _) => Task.FromResult<ChatMessageCollectionResponse?>(
                new ChatMessageCollectionResponse { Value = [Msg("in-window", T0.AddDays(1))] })));

        var result = await Window(pager, since: T0, until: T0.AddDays(7));

        Assert.Equal(["in-window"], result.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Reading_through_newer_pages_stops_at_the_cap_and_says_there_is_more()
    {
        var pages = 0;
        ChatMessageCollectionResponse Newer() => new()
        {
            Value = [Msg($"newer-{++pages}", T0.AddDays(30))],
            OdataNextLink = "https://graph.microsoft.com/next",
        };
        var pager = (
            (TeamsTools.FirstPage)(_ => Task.FromResult<ChatMessageCollectionResponse?>(Newer())),
            (TeamsTools.NextPage)((_, _) => Task.FromResult<ChatMessageCollectionResponse?>(Newer())));

        var result = await Window(pager, since: T0, until: T0.AddDays(7));

        Assert.Equal(TeamsTools.MaxUntilPages, pages);
        Assert.Empty(result.Messages);
        Assert.True(result.HasMore);
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_refused()
    {
        Assert.Throws<ModelContextProtocol.McpException>(
            () => TeamsTools.ParseWindow("2026-08-03T00:00:00Z", "2026-07-27T00:00:00Z"));
        Assert.Contains("`until`", Assert.Throws<ModelContextProtocol.McpException>(
            () => TeamsTools.ParseWindow(null, "last week")).Message);
        Assert.Equal((null, null), TeamsTools.ParseWindow(null, null));
    }
}
