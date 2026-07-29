using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The cursor carries all of a caller's resume state, so a watch depends on three things: a round
/// trip that changes nothing, a boundary that stops re-delivering, and a watermark that never steps
/// past a message the caller was not handed.
/// </summary>
public class WaitCursorTests
{
    private static MessageDto Msg(string id, DateTimeOffset created) =>
        new(id, null, null, created, "Jason Bright", "hello", null, null, null, null, null);

    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 5, 52, 7, TimeSpan.Zero);

    [Fact]
    public void A_cursor_round_trips_unchanged()
    {
        var before = new Dictionary<string, Watermark>
        {
            ["48:notes"] = new(T0, ["a", "b"]),
            ["19:x@thread.v2"] = new(T0.AddMinutes(-3), null),
        };

        var after = Cursors.Decode(Cursors.Encode(before));

        Assert.Equal(2, after.Count);
        Assert.Equal(T0, after["48:notes"].Ts);
        Assert.Equal(["a", "b"], after["48:notes"].Delivered!.OrderBy(x => x));
        Assert.Equal(T0.AddMinutes(-3), after["19:x@thread.v2"].Ts);
        Assert.Null(after["19:x@thread.v2"].Delivered);
    }

    [Fact]
    public void An_encoded_cursor_survives_being_passed_as_a_plain_argument()
    {
        var encoded = Cursors.Encode(new Dictionary<string, Watermark> { ["48:notes"] = new(T0, ["a"]) });

        // Base64url only: no +, / or = for a caller to mangle in a URL or a shell.
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void No_cursor_decodes_to_no_watermarks_rather_than_failing()
    {
        Assert.Empty(Cursors.Decode(null));
        Assert.Empty(Cursors.Decode(""));
        Assert.Empty(Cursors.Decode("   "));
    }

    [Fact]
    public void A_damaged_cursor_says_so_instead_of_silently_restarting_the_watch()
    {
        var e = Assert.Throws<McpException>(() => Cursors.Decode("not-a-cursor!!"));

        Assert.Contains("cursor", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Advance_moves_to_the_newest_delivered_message_and_keeps_its_ties()
    {
        // Two messages share the newest instant, the case a bare timestamp cannot represent.
        var next = TeamsTools.Advance(
            new Watermark(T0.AddMinutes(-1), null),
            [Msg("newest-a", T0), Msg("newest-b", T0), Msg("older", T0.AddSeconds(-30))]);

        Assert.Equal(T0, next.Ts);
        Assert.Equal(["newest-a", "newest-b"], next.Delivered!.OrderBy(x => x));
        Assert.DoesNotContain("older", next.Delivered!);
    }

    [Fact]
    public void Advance_over_nothing_leaves_the_watermark_exactly_where_it_was()
    {
        var prior = new Watermark(T0, ["a"]);

        var next = TeamsTools.Advance(prior, []);

        Assert.Equal(prior.Ts, next.Ts);
        Assert.Equal(prior.Delivered, next.Delivered);
    }

    [Fact]
    public void Merging_orders_every_source_together_newest_first()
    {
        var merged = TeamsTools.MergePages(
            [
                ("chat-a", new MessagesResult([Msg("a1", T0), Msg("a2", T0.AddMinutes(-2))], null, null)),
                ("chat-b", new MessagesResult([Msg("b1", T0.AddMinutes(-1))], null, null)),
            ],
            limit: 20, labelSource: true);

        Assert.Equal(["a1", "b1", "a2"], merged.Messages.Select(m => m.Id));
        Assert.Equal("chat-a", merged.Messages[0].ChatId);
        Assert.Equal("chat-b", merged.Messages[1].ChatId);
    }

    [Fact]
    public void A_single_source_is_not_labelled_with_a_chat_id()
    {
        var merged = TeamsTools.MergePages(
            [("chat-a", new MessagesResult([Msg("a1", T0)], null, null))],
            limit: 20, labelSource: false);

        Assert.Null(merged.Messages[0].ChatId);
    }

    [Fact]
    public void Trimming_the_merge_to_the_limit_reports_has_more()
    {
        var merged = TeamsTools.MergePages(
            [
                ("chat-a", new MessagesResult([Msg("a1", T0), Msg("a2", T0.AddMinutes(-2))], null, null)),
                ("chat-b", new MessagesResult([Msg("b1", T0.AddMinutes(-1))], null, null)),
            ],
            limit: 2, labelSource: true);

        Assert.True(merged.HasMore);
        Assert.Equal(["a1", "b1"], merged.Messages.Select(m => m.Id));
    }

    [Fact]
    public void A_source_trimmed_out_of_the_answer_does_not_advance_its_cursor()
    {
        // chat-a's older message is trimmed out of the answer, so its watermark must stay put or
        // the next poll skips a message the caller never received.
        var merged = TeamsTools.MergePages(
            [
                ("chat-a", new MessagesResult([Msg("a2", T0.AddMinutes(-2))], null, null)),
                ("chat-b", new MessagesResult([Msg("b1", T0)], null, null)),
            ],
            limit: 1, labelSource: true);

        Assert.False(merged.BySource.ContainsKey("chat-a"));
        Assert.Equal(["b1"], merged.BySource["chat-b"].Select(m => m.Id));
    }

    [Fact]
    public void Skip_counts_from_every_source_are_added_up()
    {
        var merged = TeamsTools.MergePages(
            [
                ("chat-a", new MessagesResult([], null, new SkippedDto(1, 2))),
                ("chat-b", new MessagesResult([], null, new SkippedDto(null, 3))),
            ],
            limit: 20, labelSource: true);

        Assert.Equal(1, merged.Skipped!.Deleted);
        Assert.Equal(5, merged.Skipped!.System);
    }

    [Fact]
    public void One_source_reporting_has_more_carries_it_to_the_merged_answer()
    {
        var merged = TeamsTools.MergePages(
            [
                ("chat-a", new MessagesResult([Msg("a1", T0)], true, null)),
                ("chat-b", new MessagesResult([], null, null)),
            ],
            limit: 20, labelSource: true);

        Assert.True(merged.HasMore);
    }

    [Fact]
    public void Chat_targets_take_either_argument_and_drop_duplicates()
    {
        Assert.Equal(["one"], TeamsTools.ChatTargets("one", null));
        Assert.Equal(["one", "two"], TeamsTools.ChatTargets(null, ["one", "two"]));
        Assert.Equal(["one", "two"], TeamsTools.ChatTargets("one", ["one", "two"]));
        Assert.Equal(["one", "two"], TeamsTools.ChatTargets(" one ", ["two", "ONE"]));
    }

    [Fact]
    public void Naming_no_chat_at_all_is_a_refusal_rather_than_an_empty_wait()
    {
        var e = Assert.Throws<McpException>(() => TeamsTools.ChatTargets(null, null));

        Assert.Contains("chats", e.Message);
        Assert.Throws<McpException>(() => TeamsTools.ChatTargets("  ", []));
    }

    [Fact]
    public void More_chats_than_one_wait_can_poll_is_a_refusal_that_says_the_limit()
    {
        var many = Enumerable.Range(0, 21).Select(i => $"chat-{i}").ToArray();

        var e = Assert.Throws<McpException>(() => TeamsTools.ChatTargets(null, many));

        Assert.Contains("21", e.Message);
        Assert.Contains("20", e.Message);
    }
}
