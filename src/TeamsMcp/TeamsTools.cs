using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Search.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TeamsMcp;

// Output conventions, chosen to keep results small in a model's context window:
// - Null fields are omitted from serialized results (configured in Program.cs).
// - messageType is only emitted when the message is not a normal user message.
// - System-event messages (member added, renamed, ...) and deleted messages are
//   skipped by default and counted in the `skipped` envelope field.
// - Bodies are plain text (links kept as "text (url)"), truncated at body_limit
//   with `truncated: true`.
[McpServerToolType]
public sealed partial class TeamsTools(GraphContext graph, ILogger<TeamsTools> log)
{
    /// <summary>Shorthand for the logging helper. Every tool logs its arguments through it.</summary>
    private static string A(string name, object? value) => TeamsMcpLog.Arg(name, value);

    // ---------------------------------------------------------------- read tools

    [McpServerTool(Name = "list_teams", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the Teams teams the signed-in user has joined. Returns id, name, description.")]
    public Task<List<TeamDto>> ListTeams(CancellationToken ct) => Run("list_teams", "", async () =>
    {
        var client = await graph.GetClientAsync(ct);
        return await ListTeamsInternal(client, ct);
    });

    [McpServerTool(Name = "list_channels", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List channels of a team. `team` may be a team id (GUID) or a display name.")]
    public Task<List<ChannelDto>> ListChannels(
        [Description("Team id (GUID) or display name")] string team,
        CancellationToken ct) => Run("list_channels", A("team", team), async () =>
    {
        var client = await graph.GetClientAsync(ct);
        var (teamId, _) = await ResolveTeamAsync(client, team, log, ct);
        var page = await client.Teams[teamId].Channels.GetAsync(cancellationToken: ct);
        return (page?.Value ?? []).Select(c => new ChannelDto(
            c.Id, c.DisplayName, TrimDescription(c.Description, c.DisplayName),
            c.MembershipType == ChannelMembershipType.Standard ? null : c.MembershipType?.ToString())).ToList();
    });

    [McpServerTool(Name = "list_chats", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the signed-in user's 1:1 and group chats, most recently active first. " +
                 "Filter by `member` (person's display name) or `topic` to find a chat without knowing its id.")]
    public Task<List<ChatDto>> ListChats(
        [Description("Only chats that include a member whose display name contains this (case-insensitive)")] string? member = null,
        [Description("Only chats whose topic contains this (case-insensitive)")] string? topic = null,
        [Description("Maximum chats to return (default 50)")] int limit = 50,
        CancellationToken ct = default) => Run("list_chats",
        A("member", member) + A("topic", topic) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        const int scanCap = 500; // upper bound on chats examined when filtering
        var client = await graph.GetClientAsync(ct);
        var results = new List<ChatDto>();
        var scanned = 0;
        var page = await client.Me.Chats.GetAsync(rc =>
        {
            rc.QueryParameters.Expand = ["members"];
            rc.QueryParameters.Top = 50;
            rc.QueryParameters.Orderby = ["lastMessagePreview/createdDateTime desc"];
        }, ct);
        while (page is not null)
        {
            foreach (var chat in page.Value ?? [])
            {
                scanned++;
                var members = (chat.Members ?? [])
                    .Select(m => m.DisplayName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                if (member is not null &&
                    !members.Any(n => n!.Contains(member, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (topic is not null &&
                    chat.Topic?.Contains(topic, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }
                results.Add(new ChatDto(
                    chat.Id, chat.Topic, chat.ChatType?.ToString(), chat.LastUpdatedDateTime, members!));
                if (results.Count >= limit)
                {
                    return results;
                }
            }
            if (page.OdataNextLink is null || scanned >= scanCap)
            {
                if (scanned >= scanCap)
                {
                    log.Line(LogLevel.Warning, Ev.Page,
                        "list_chats hit the scan cap; results may be incomplete" +
                        A("scanned", scanned) + A("cap", scanCap) + A("matched", results.Count));
                }
                break;
            }
            log.Line(LogLevel.Debug, Ev.Page,
                "list_chats next page" + A("scanned", scanned) + A("matched", results.Count));
            page = await client.Me.Chats.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }
        return results;
    });

    [McpServerTool(Name = "read_channel_messages", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read messages of a team channel, newest first. Set include_replies=true to get each " +
                 "root message's reply thread nested under it. `team`/`channel` accept ids or display names. " +
                 "Returns {messages, hasMore?, skipped?}.")]
    public Task<MessagesResult> ReadChannelMessages(
        [Description("Team id (GUID) or display name")] string team,
        [Description("Channel id (19:...) or display name")] string channel,
        [Description("Only messages created at/after this ISO-8601 timestamp, e.g. 2026-07-01T00:00:00Z")] string? since = null,
        [Description("Maximum root messages to return (default 20, max 200)")] int limit = 20,
        [Description("Nest each root message's replies under it (default false)")] bool include_replies = false,
        [Description("Include system event messages such as member-added (default false; skipped ones are counted)")] bool include_system = false,
        [Description("Max characters per message body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("read_channel_messages",
        A("team", team) + A("channel", channel) + A("since", since) + A("limit", limit) +
        A("include_replies", include_replies) + A("include_system", include_system) + A("body_limit", body_limit),
        async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var sinceTs = ParseSince(since);
        var client = await graph.GetClientAsync(ct);
        var (teamId, _) = await ResolveTeamAsync(client, team, log, ct);
        var channelId = await ResolveChannelAsync(client, teamId, channel, log, ct);

        return await PageMessagesAsync(
            ChannelPager(client, teamId, channelId, include_replies),
            Watermark.From(sinceTs), limit, include_replies, include_system, body_limit, ct);
    });

    /// <summary>
    /// A pager is the first request plus the follow-the-link request for one Graph message
    /// collection. Channel and chat messages answer with the same
    /// <c>ChatMessageCollectionResponse</c> and the same <c>OdataNextLink</c>, so only the first
    /// request differs.
    /// </summary>
    internal delegate Task<ChatMessageCollectionResponse?> FirstPage(CancellationToken ct);

    internal delegate Task<ChatMessageCollectionResponse?> NextPage(string url, CancellationToken ct);

    private static (FirstPage First, NextPage Next) ChannelPager(
        GraphServiceClient client, string teamId, string channelId, bool includeReplies) =>
        (ct => client.Teams[teamId].Channels[channelId].Messages.GetAsync(rc =>
            {
                rc.QueryParameters.Top = 50;
                if (includeReplies)
                {
                    rc.QueryParameters.Expand = ["replies"];
                }
            }, ct),
         (url, ct) => client.Teams[teamId].Channels[channelId].Messages
            .WithUrl(url).GetAsync(cancellationToken: ct));

    private static (FirstPage First, NextPage Next) ChatPager(GraphServiceClient client, string chat) =>
        (ct => client.Chats[chat].Messages.GetAsync(rc => rc.QueryParameters.Top = 50, ct),
         (url, ct) => client.Chats[chat].Messages.WithUrl(url).GetAsync(cancellationToken: ct));

    /// <summary>
    /// Walks a message collection, mapping and skip-counting as it goes. Stops at
    /// <paramref name="limit"/> results (which sets <c>hasMore</c>) or at the first page holding
    /// nothing at or after <paramref name="floor"/>. Shared by the read tools and the waiters so
    /// both return the same shape.
    ///
    /// <para>The stop is per page rather than per message because Graph orders these collections
    /// by <c>lastModifiedDateTime</c>, not <c>createdDateTime</c>, and a reaction moves the former.
    /// A single old message can therefore surface at the top of the listing with nothing new having
    /// been said, so "older than the floor" is a reason to skip that message, never a reason to
    /// conclude the newer ones are exhausted. A whole page of them still ends the scan, which is
    /// what keeps a recent floor from walking the entire conversation.</para>
    /// </summary>
    internal static async Task<MessagesResult> PageMessagesAsync(
        (FirstPage First, NextPage Next) pager, Watermark? floor, int limit,
        bool includeReplies, bool includeSystem, int bodyLimit, CancellationToken ct)
    {
        var counts = new SkipCounter();
        var results = new List<MessageDto>();
        var hasMore = false;
        var done = false;
        var page = await pager.First(ct);
        while (!done && page is not null)
        {
            var reachedFloorOnThisPage = floor is null;
            foreach (var msg in page.Value ?? [])
            {
                if (results.Count >= limit)
                {
                    hasMore = true;
                    done = true;
                    break;
                }
                if (floor is { } f)
                {
                    if (msg.CreatedDateTime < f.Ts)
                    {
                        // Out of range, but not proof the range is exhausted: the listing is
                        // ordered by lastModifiedDateTime, so a reaction can lift this above
                        // messages that genuinely are newer. Skip it and read on.
                        continue;
                    }
                    reachedFloorOnThisPage = true;
                    // The boundary is inclusive, so a cursor also lists the ids already delivered
                    // at exactly that instant. Skipping them keeps the newest message from coming
                    // back on every poll.
                    if (msg.CreatedDateTime == f.Ts && msg.Id is { } seen &&
                        f.Delivered?.Contains(seen) == true)
                    {
                        continue;
                    }
                }
                if (Map(msg, includeSystem, bodyLimit, includeReplies, counts) is { } dto)
                {
                    results.Add(dto);
                }
            }
            // A page with nothing at or after the floor is the end of the newer messages. One
            // stray out-of-order message is not, which is the whole of the difference.
            if (done || !reachedFloorOnThisPage || page.OdataNextLink is null)
            {
                break;
            }
            page = await pager.Next(page.OdataNextLink, ct);
        }
        return new MessagesResult(results, hasMore ? true : null, counts.ToDto());
    }

    [McpServerTool(Name = "read_chat_messages", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read messages of a 1:1 or group chat, newest first. `chat` is a chat id from " +
                 "list_chats (use its member/topic filters to find one by name). Returns {messages, hasMore?, skipped?}.")]
    public Task<MessagesResult> ReadChatMessages(
        [Description("Chat id, e.g. 19:...@thread.v2")] string chat,
        [Description("Only messages created at/after this ISO-8601 timestamp")] string? since = null,
        [Description("Maximum messages to return (default 20, max 200)")] int limit = 20,
        [Description("Include system event messages such as member-added (default false; skipped ones are counted)")] bool include_system = false,
        [Description("Max characters per message body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("read_chat_messages",
        A("chat", chat) + A("since", since) + A("limit", limit) +
        A("include_system", include_system) + A("body_limit", body_limit),
        async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var sinceTs = ParseSince(since);
        var client = await graph.GetClientAsync(ct);

        return await PageMessagesAsync(
            ChatPager(client, chat), Watermark.From(sinceTs), limit,
            includeReplies: false, include_system, body_limit, ct);
    });

    // ---------------------------------------------------------------- waiting
    //
    // Both waiters run the same loop over a different surface: poll for anything newer than a
    // watermark, return as soon as something arrives, and give up when the clock runs out. A wait
    // that runs out of time returns normally with `timedOut: true`, because "nobody said anything"
    // is a real answer.
    //
    // Every wait returns a `nextCursor`, timeouts included. Passing it back unchanged resumes the
    // watch exactly, with no seen-id bookkeeping on the caller's side. (A caller resuming from a
    // timestamp gets the boundary message again forever, since Graph's boundary is inclusive.)
    // The chat waiter accepts several chats and polls them concurrently, so a caller that can only
    // block once can still watch many conversations.

    private const int MinPollSeconds = 5;

    /// <summary>
    /// How many chats one wait may watch. Every target costs a Graph call on every poll, so the
    /// list is bounded.
    /// </summary>
    private const int MaxWaitChats = 20;

    /// <summary>Cursor semantics, written once and repeated into each waiter's description.</summary>
    private const string CursorHelp =
        "`nextCursor` is always returned, including on a timeout: pass it back as `cursor` on the " +
        "next call and nothing is repeated, so no seen-id bookkeeping is needed on the caller's " +
        "side. `hasMore: true` means the answer was trimmed to `limit` — call again immediately " +
        "with the cursor to drain the rest, rather than waiting for the next arrival. A single " +
        "conversation that produced more than `limit` at once is the one lossy case: its cursor " +
        "moves to the newest message delivered, so raise `limit` if bursts that big matter. ";

    [McpServerTool(Name = "wait_for_channel_messages", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for new messages in a team channel and return them as soon as any " +
                 "arrive. Waits for messages newer than `cursor` if given, else `since`, else the " +
                 "moment the call starts, so it reports what is said from now on rather than what is " +
                 "already there. Running out of `timeout_seconds` is not an error: it returns no " +
                 "messages and `timedOut: true`. " + CursorHelp +
                 "Returns {messages, hasMore?, skipped?, waitedSeconds, timedOut?, nextCursor}.")]
    public Task<MessagesWaitResult> WaitForChannelMessages(
        [Description("Team id (GUID) or display name")] string team,
        [Description("Channel id (19:...) or display name")] string channel,
        [Description("Wait for messages at/after this ISO-8601 timestamp; defaults to now")] string? since = null,
        [Description("Opaque nextCursor from a previous call; overrides `since` and resumes exactly where that call stopped")] string? cursor = null,
        [Description("Give up after this many seconds (default 300, max 3600)")] int timeout_seconds = 300,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Maximum messages to return (default 20, max 200)")] int limit = 20,
        [Description("Nest each root message's replies under it (default false)")] bool include_replies = false,
        [Description("Include system event messages such as member-added (default false)")] bool include_system = false,
        [Description("Max characters per message body (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("wait_for_channel_messages",
        A("team", team) + A("channel", channel) + A("since", since) + A("cursor", cursor) +
        A("timeout_seconds", timeout_seconds) + A("poll_seconds", poll_seconds) + A("limit", limit) +
        A("include_replies", include_replies) + A("include_system", include_system) + A("body_limit", body_limit),
        async () =>
    {
        var client = await graph.GetClientAsync(ct);
        var (teamId, _) = await ResolveTeamAsync(client, team, log, ct);
        var channelId = await ResolveChannelAsync(client, teamId, channel, log, ct);
        return await WaitForNewAsync(
            [(channelId, ChannelPager(client, teamId, channelId, include_replies))],
            since, cursor, timeout_seconds, poll_seconds, MinPollSeconds, Math.Clamp(limit, 1, 200),
            include_replies, include_system, body_limit, labelSource: false, ct);
    });

    [McpServerTool(Name = "wait_for_chat_messages", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for new messages in one or more 1:1 or group chats and return them " +
                 "as soon as any arrive. Pass a single `chat`, or `chats` to watch several at once — " +
                 "they are polled concurrently and one call covers all of them, which is what makes " +
                 "watching many conversations possible from a caller that can only block once. Chat " +
                 "ids come from list_chats. Each message carries `chatId` when more than one chat is " +
                 "being watched. Waits for messages newer than `cursor` if given, else `since`, else " +
                 "the moment the call starts. Running out of `timeout_seconds` is not an error: it " +
                 "returns no messages and `timedOut: true`. " + CursorHelp +
                 "Returns {messages, hasMore?, skipped?, waitedSeconds, timedOut?, nextCursor}.")]
    public Task<MessagesWaitResult> WaitForChatMessages(
        [Description("Chat id, e.g. 19:...@thread.v2. Give this or `chats`.")] string? chat = null,
        [Description("Several chat ids to watch in one call, max 20. Give this or `chat`.")] string[]? chats = null,
        [Description("Wait for messages at/after this ISO-8601 timestamp; defaults to now")] string? since = null,
        [Description("Opaque nextCursor from a previous call; overrides `since` and resumes exactly where that call stopped")] string? cursor = null,
        [Description("Give up after this many seconds (default 300, max 3600)")] int timeout_seconds = 300,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Maximum messages to return across all the chats (default 20, max 200)")] int limit = 20,
        [Description("Include system event messages such as member-added (default false)")] bool include_system = false,
        [Description("Max characters per message body (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("wait_for_chat_messages",
        A("chat", chat) + A("chats", chats is null ? null : string.Join(",", chats)) +
        A("since", since) + A("cursor", cursor) + A("timeout_seconds", timeout_seconds) +
        A("poll_seconds", poll_seconds) + A("limit", limit) +
        A("include_system", include_system) + A("body_limit", body_limit),
        async () =>
    {
        var targets = ChatTargets(chat, chats);
        var client = await graph.GetClientAsync(ct);
        return await WaitForNewAsync(
            [.. targets.Select(id => (id, ChatPager(client, id)))],
            since, cursor, timeout_seconds, poll_seconds, MinPollSeconds, Math.Clamp(limit, 1, 200),
            includeReplies: false, include_system, body_limit,
            labelSource: targets.Count > 1, ct);
    });

    /// <summary>
    /// The union of <c>chat</c> and <c>chats</c>, deduplicated, in the order given. Both are
    /// optional so a caller with one conversation needs no array and one with twenty needs no
    /// twenty calls. Passing neither is an error.
    /// </summary>
    internal static List<string> ChatTargets(string? chat, string[]? chats)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>();
        foreach (var id in chats is null ? [chat] : chats.Prepend(chat))
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id.Trim()))
            {
                targets.Add(id.Trim());
            }
        }

        if (targets.Count == 0)
        {
            throw new McpException("Pass `chat` for one conversation, or `chats` for several.");
        }
        if (targets.Count > MaxWaitChats)
        {
            throw new McpException(
                $"Too many chats for one wait: {targets.Count}, and the limit is {MaxWaitChats}. " +
                "Each one is polled separately, so split them across calls or narrow the list.");
        }
        return targets;
    }

    /// <summary>
    /// Watches one or more message collections for anything newer than their watermarks.
    /// <see cref="PollAsync{T}"/> does the waiting. This decides what a poll asks for, how several
    /// sources merge into one answer, and how the cursor advances.
    /// </summary>
    private async Task<MessagesWaitResult> WaitForNewAsync(
        IReadOnlyList<(string Id, (FirstPage First, NextPage Next) Pager)> sources,
        string? since, string? cursor, int timeoutSeconds, int pollSeconds, int minPollSeconds,
        int limit, bool includeReplies, bool includeSystem, int bodyLimit, bool labelSource,
        CancellationToken ct)
    {
        // No cursor and no `since` means watch from now. Defaulting to the start of the
        // conversation would return the backlog instantly instead of waiting.
        var anchor = ParseSince(since) ?? DateTimeOffset.UtcNow;
        var resumed = Cursors.Decode(cursor);
        var floors = sources.ToDictionary(
            s => s.Id,
            s => resumed.TryGetValue(s.Id, out var w) ? w : new Watermark(anchor, null),
            StringComparer.OrdinalIgnoreCase);

        var (found, waited, timedOut) = await PollAsync(
            probe: async token =>
            {
                var pages = await Task.WhenAll(sources.Select(async s =>
                    (s.Id, Page: await PageMessagesAsync(
                        s.Pager, floors[s.Id], limit, includeReplies, includeSystem, bodyLimit, token))));
                return MergePages(pages, limit, labelSource);
            },
            count: r => r.Messages.Count,
            timeoutSeconds, pollSeconds, minPollSeconds, ct);

        // The cursor advances only over messages actually returned, never over what a probe merely
        // saw, so trimming the merge to `limit` cannot skip past a source's messages. A timeout
        // advances nothing and still returns the cursor.
        var next = new Dictionary<string, Watermark>(floors, StringComparer.OrdinalIgnoreCase);
        if (!timedOut)
        {
            foreach (var (source, delivered) in found.BySource)
            {
                next[source] = Advance(floors.GetValueOrDefault(source), delivered);
            }
        }
        var nextCursor = Cursors.Encode(next);

        return timedOut
            ? new MessagesWaitResult([], null, found.Skipped, waited, TimedOut: true, NextCursor: nextCursor)
            : new MessagesWaitResult(found.Messages, found.HasMore, found.Skipped, waited, null, nextCursor);
    }

    /// <summary>
    /// Merges one poll of several sources into a single newest-first list. <c>limit</c> applies to
    /// the merged list, so a busy conversation cannot starve a quiet one: a source whose messages
    /// were all trimmed out delivered nothing, its watermark stays put, and it drains on the next
    /// call. The one lossy case is a source that delivered part of a burst. Its watermark advances
    /// to the newest delivered message and the rest of that burst is skipped.
    /// </summary>
    internal static MergedPage MergePages(
        IEnumerable<(string Id, MessagesResult Page)> pages, int limit, bool labelSource)
    {
        var all = new List<(string Source, MessageDto Dto)>();
        var deleted = 0;
        var system = 0;
        var hasMore = false;

        foreach (var (id, page) in pages)
        {
            hasMore |= page.HasMore == true;
            deleted += page.Skipped?.Deleted ?? 0;
            system += page.Skipped?.System ?? 0;
            foreach (var dto in page.Messages)
            {
                all.Add((id, labelSource ? dto with { ChatId = id } : dto));
            }
        }

        var ordered = all.OrderByDescending(m => m.Dto.Created ?? DateTimeOffset.MinValue).ToList();
        if (ordered.Count > limit)
        {
            hasMore = true;
            ordered = [.. ordered.Take(limit)];
        }

        var bySource = new Dictionary<string, List<MessageDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, dto) in ordered)
        {
            if (!bySource.TryGetValue(source, out var list))
            {
                bySource[source] = list = [];
            }
            list.Add(dto);
        }

        return new MergedPage(
            [.. ordered.Select(m => m.Dto)],
            hasMore ? true : null,
            deleted == 0 && system == 0
                ? null
                : new SkippedDto(deleted == 0 ? null : deleted, system == 0 ? null : system),
            bySource);
    }

    /// <summary>
    /// Moves a watermark to the newest delivered message, remembering every id at that exact
    /// instant. When nothing was delivered the watermark stays put.
    /// </summary>
    internal static Watermark Advance(Watermark? prior, IReadOnlyList<MessageDto> delivered)
    {
        var newest = delivered.Select(m => m.Created).Max();
        if (newest is null)
        {
            return prior ?? new Watermark(DateTimeOffset.UtcNow, null);
        }

        return new Watermark(
            newest.Value,
            [.. delivered.Where(m => m.Created == newest && m.Id is not null).Select(m => m.Id!)]);
    }

    /// <summary>
    /// The shared wait loop: probe, return as soon as the probe finds anything, give up when the
    /// clock runs out. Timeout and interval are clamped, so a caller cannot wait forever or poll
    /// hard enough to matter to Graph. A timed-out wait returns the last probe's result, so what
    /// it learned along the way (a skip count, a total) survives.
    /// </summary>
    private async Task<(T Found, int WaitedSeconds, bool TimedOut)> PollAsync<T>(
        Func<CancellationToken, Task<T>> probe, Func<T, int> count,
        int timeoutSeconds, int pollSeconds, int minPollSeconds, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, minPollSeconds, 600));

        var sw = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            var found = await probe(ct);
            polls++;
            if (count(found) > 0)
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "messages arrived" + A("count", count(found)) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return (found, (int)sw.Elapsed.TotalSeconds, false);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return (found, (int)sw.Elapsed.TotalSeconds, true);
            }

            await Task.Delay(interval < left ? interval : left, ct);
        }
    }

    // ------------------------------------------------------------------- search
    //
    // The tools below are one mechanism, the Microsoft Search API over chatMessage, asked
    // different questions. It is the only delegated surface that spans chats and team channels in
    // a single request: there is no "all my messages" endpoint, and walking every conversation
    // would be an unbounded scan. The trade is freshness and detail. Hits come from an index that
    // lags the conversation and carry a summary instead of a body, so these tools locate a
    // message and the read tools fetch it.

    /// <summary>KQL scope terms, written once and repeated into each search tool's description.</summary>
    private const string KqlHelp =
        "KQL is supported: from:Alice, to:Jason, mentions:<user id>, IsMentioned:true, IsRead:false, " +
        "hasAttachment:true, sent>2026-07-01, \"exact phrase\", AND/OR/NOT. ";

    private const string HitHelp =
        "Hits carry a summary rather than the full body (Graph serves no body for a search hit) plus " +
        "chatId, or teamId+channelId for a channel message — follow one up with read_chat_messages " +
        "or read_channel_messages to get the text. Returns {hits, hasMore?, total?}.";

    [McpServerTool(Name = "search_messages", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text search across the signed-in user's Teams messages, spanning both chats " +
                 "and the channels of teams they are in. " + KqlHelp + HitHelp)]
    public Task<SearchResult> SearchMessages(
        [Description("Search query, e.g. \"deploy from:Alice\"")] string query,
        [Description("Only messages created at/after this ISO-8601 timestamp, e.g. 2026-07-01T00:00:00Z")] string? since = null,
        [Description("Maximum hits to return (default 25, max 100)")] int limit = 25,
        [Description("Max characters per hit summary; longer ones get truncated:true (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("search_messages",
        TeamsMcpLog.ContentArg("query", query) + A("since", since) + A("limit", limit) + A("body_limit", body_limit),
        async () => await SearchAsync(query, ParseSince(since), mentionsOnly: false, limit, body_limit, ct));

    [McpServerTool(Name = "list_mentions", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Messages that @-mention the signed-in user, across every chat and every channel " +
                 "of the teams they are in, newest first. This is the inbox-style 'what needs me' question; " +
                 "search_messages is the same index without the mention filter. Narrow it further with `query` (" +
                 KqlHelp + ") or `since`. " + HitHelp)]
    public Task<SearchResult> ListMentions(
        [Description("Optional extra terms to narrow the mentions, e.g. \"from:Alice\" or \"invoice\"")] string? query = null,
        [Description("Only mentions created at/after this ISO-8601 timestamp")] string? since = null,
        [Description("Maximum hits to return (default 25, max 100)")] int limit = 25,
        [Description("Max characters per hit summary; longer ones get truncated:true (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("list_mentions",
        TeamsMcpLog.ContentArg("query", query) + A("since", since) + A("limit", limit) + A("body_limit", body_limit),
        async () => await SearchAsync(query, ParseSince(since), mentionsOnly: true, limit, body_limit, ct));

    [McpServerTool(Name = "wait_for_mentions", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait until somebody @-mentions the signed-in user anywhere in Teams — any chat, " +
                 "any channel of a team they are in — and return the mentions as soon as any arrive. Waits for " +
                 "mentions newer than `since`, defaulting to the moment the call starts. Backed by the search " +
                 "index, which trails the conversation by seconds and occasionally longer, so this reports an " +
                 "arrival later than wait_for_chat_messages does on a chat it is already pointed at. Running out of " +
                 "`timeout_seconds` is not an error: it returns no hits and `timedOut: true`. " +
                 "Returns {hits, hasMore?, total?, waitedSeconds, timedOut?}.")]
    public Task<SearchWaitResult> WaitForMentions(
        [Description("Optional extra terms to narrow the mentions, e.g. \"from:Alice\"")] string? query = null,
        [Description("Wait for mentions at/after this ISO-8601 timestamp; defaults to now")] string? since = null,
        [Description("Give up after this many seconds (default 900, max 3600)")] int timeout_seconds = 900,
        [Description("Seconds between checks (default 60, min 20 — the index will not have moved faster)")] int poll_seconds = 60,
        [Description("Maximum hits to return (default 25, max 100)")] int limit = 25,
        [Description("Max characters per hit summary (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("wait_for_mentions",
        TeamsMcpLog.ContentArg("query", query) + A("since", since) + A("timeout_seconds", timeout_seconds) +
        A("poll_seconds", poll_seconds) + A("limit", limit) + A("body_limit", body_limit),
        async () => await WaitForSearchAsync(
            query, since, mentionsOnly: true, timeout_seconds, poll_seconds, limit, body_limit, ct));

    [McpServerTool(Name = "wait_for_any_message", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for the next message anywhere in Teams — any chat, any channel of a team the " +
                 "signed-in user is in — and return it as soon as it arrives. Use this when the question is " +
                 "'tell me when anything happens'; use wait_for_chat_messages / wait_for_channel_messages when " +
                 "the conversation is already known, and wait_for_mentions when only being addressed counts. " +
                 "Narrow with `query` (" + KqlHelp + "). Backed by the search index, which trails the " +
                 "conversation by seconds and occasionally longer. Running out of `timeout_seconds` is not an " +
                 "error: it returns no hits and " +
                 "`timedOut: true`. Returns {hits, hasMore?, total?, waitedSeconds, timedOut?}.")]
    public Task<SearchWaitResult> WaitForAnyMessage(
        [Description("Optional terms to narrow what counts, e.g. \"from:Alice\" or \"deploy\"")] string? query = null,
        [Description("Wait for messages at/after this ISO-8601 timestamp; defaults to now")] string? since = null,
        [Description("Give up after this many seconds (default 900, max 3600)")] int timeout_seconds = 900,
        [Description("Seconds between checks (default 60, min 20 — the index will not have moved faster)")] int poll_seconds = 60,
        [Description("Maximum hits to return (default 25, max 100)")] int limit = 25,
        [Description("Max characters per hit summary (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("wait_for_any_message",
        TeamsMcpLog.ContentArg("query", query) + A("since", since) + A("timeout_seconds", timeout_seconds) +
        A("poll_seconds", poll_seconds) + A("limit", limit) + A("body_limit", body_limit),
        async () => await WaitForSearchAsync(
            query, since, mentionsOnly: false, timeout_seconds, poll_seconds, limit, body_limit, ct));

    /// <summary>
    /// Pages the search index for one question. Stops at <paramref name="limit"/> hits, at the end
    /// of the results, or at <see cref="MaxSearchPages"/> pages. Hitting the page cap is reported
    /// as hasMore plus a warning instead of being passed off as a complete answer.
    /// </summary>
    private async Task<SearchResult> SearchAsync(
        string? query, DateTimeOffset? since, bool mentionsOnly, int limit, int bodyLimit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var kql = SearchQueries.Build(query, since, mentionsOnly);
        if (kql.Length == 0)
        {
            throw new McpException("A search needs something to search for: pass `query`, or `since`.");
        }

        var client = await graph.GetClientAsync(ct);
        var hits = new List<SearchHitDto>();
        var hasMore = false;
        int? total = null;
        var pages = 0;
        var moreAvailable = false;

        while (pages < MaxSearchPages)
        {
            var response = await client.Search.Query.PostAsQueryPostResponseAsync(new QueryPostRequestBody
            {
                Requests =
                [
                    new SearchRequest
                    {
                        EntityTypes = [EntityType.ChatMessage],
                        Query = new SearchQuery { QueryString = kql },
                        From = pages * SearchPageSize,
                        Size = SearchPageSize,
                    },
                ],
            }, cancellationToken: ct);
            pages++;

            var container = (response?.Value ?? [])
                .SelectMany(r => r.HitsContainers ?? [])
                .FirstOrDefault();
            total ??= container?.Total;
            moreAvailable = container?.MoreResultsAvailable is true;

            var page = container?.Hits ?? [];
            foreach (var hit in page)
            {
                if (hits.Count >= limit)
                {
                    hasMore = true;
                    break;
                }
                var dto = SearchQueries.Map(hit, bodyLimit);
                if (SearchQueries.IsAtOrAfter(dto, since))
                {
                    hits.Add(dto);
                }
            }

            if (hasMore || page.Count == 0 || !moreAvailable)
            {
                break;
            }
        }

        if (!hasMore && moreAvailable && pages >= MaxSearchPages)
        {
            log.Line(LogLevel.Warning, Ev.Page,
                "search hit the page cap; results may be incomplete" +
                A("pages", pages) + A("examined", pages * SearchPageSize) + A("matched", hits.Count));
            hasMore = true;
        }

        return new SearchResult(hits, hasMore ? true : null, total);
    }

    /// <summary>Hits per search request, and the cap on how many of those requests one call makes.</summary>
    private const int SearchPageSize = 25;

    private const int MaxSearchPages = 8;

    private async Task<SearchWaitResult> WaitForSearchAsync(
        string? query, string? since, bool mentionsOnly, int timeoutSeconds, int pollSeconds,
        int limit, int bodyLimit, CancellationToken ct)
    {
        // As with the message waiters, no `since` means watch from now.
        var watermark = ParseSince(since) ?? DateTimeOffset.UtcNow;
        var (found, waited, timedOut) = await PollAsync(
            probe: token => SearchAsync(query, watermark, mentionsOnly, limit, bodyLimit, token),
            count: r => r.Hits.Count,
            timeoutSeconds, pollSeconds, MinSearchPollSeconds, ct);

        // The total is dropped on a timeout. It counts what the day-granular `sent>` scope
        // matched, which is more than the caller waited for, so returning it with zero hits would
        // read as a contradiction.
        return timedOut
            ? new SearchWaitResult([], null, null, waited, TimedOut: true)
            : new SearchWaitResult(found.Hits, found.HasMore, found.Total, waited, null);
    }

    /// <summary>
    /// A search poll costs the service a real query and the index it reads moves slowly, so these
    /// waiters have a higher floor than the ones reading a known conversation.
    /// </summary>
    private const int MinSearchPollSeconds = 20;

    // ------------------------------------------------------------- mutating tools

    // A send posts a new message and edits nothing, so it is additive rather than destructive.
    // It is not idempotent: the same call twice puts the same text in the conversation twice.
    //
    // The content parameter is `body` because that is what the rest of the server calls it: reads
    // return `body`, they take `body_limit`, and the parameter's own description said "message
    // body" while the parameter was named `text`. A caller that had just read a conversation
    // supplied `body`, which cost a rejection and a re-send of the whole message — the schema was
    // the only place the word `text` appeared, and `format: "text"` is a different thing again.
    // One word for message content, both directions.
    // A channel reply is threading rather than quoting: the replies collection is what puts the
    // message inside the thread the client draws under its root, so `reply_to` posts there. It takes
    // the thread ROOT's id, the same id `react_to_channel_message` wants and the same one a reply's
    // own `replyToId` names — replying to a reply still lands in the one thread, because a channel
    // thread is one level deep. Chats have no equivalent and quote instead; see send_chat_message.
    [McpServerTool(Name = "send_channel_message", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("MUTATION: posts a message visible to everyone in the channel. Disabled unless the environment " +
                 "variable TEAMS_MCP_ALLOW_SEND=true is set for this server. `team`/`channel` accept ids or display " +
                 "names. Set reply_to to a thread root's message id to post inside that thread rather than " +
                 "starting a new one.")]
    public Task<SentMessageDto> SendChannelMessage(
        [Description("Team id (GUID) or display name")] string team,
        [Description("Channel id (19:...) or display name")] string channel,
        [Description("Message body. Plain text unless format says otherwise.")] string body,
        [Description("Body format: 'text' (default), 'markdown', or 'html'. Use markdown for anything beyond " +
                     "a single plain paragraph (Teams collapses newlines in a text body); it is converted " +
                     "server-side and renders consistently. html is a last resort.")] string? format = null,
        [Description("Id of the thread root to reply under, from read_channel_messages. Omit to start a new " +
                     "thread. To answer a reply, pass its thread root's id — the id in its replyToId.")] string? reply_to = null,
        CancellationToken ct = default) => Run("send_channel_message",
        A("team", team) + A("channel", channel) + A("format", format ?? "text") + A("reply_to", reply_to)
            + TeamsMcpLog.ContentArg("body", body), async () =>
    {
        RequireSendEnabled();
        var client = await graph.GetClientAsync(ct);
        var (teamId, _) = await ResolveTeamAsync(client, team, log, ct);
        var channelId = await ResolveChannelAsync(client, teamId, channel, log, ct);
        var messages = client.Teams[teamId].Channels[channelId].Messages;
        var message = new ChatMessage { Body = BuildBody(body, format) };
        var created = reply_to is null
            ? await messages.PostAsync(message, cancellationToken: ct)
            : await messages[reply_to].Replies.PostAsync(message, cancellationToken: ct);
        return new SentMessageDto(created?.Id, created?.CreatedDateTime, created?.WebUrl);
    });

    // A chat has no thread to reply into: Teams' chat "Reply" is a quote, carried by a
    // `messageReference` attachment that an `<attachment>` element in the body anchors. Only
    // `replyWithQuote` creates that attachment, which is why `reply_to` branches to another endpoint
    // rather than decorating the ChatMessage. Two measured dead ends, both of which post a message
    // that looks sent and renders as an empty box above the text:
    //   - Composing the attachment here and posting it normally. Graph strips a `messageReference`
    //     off a posted message — tried with the quoted message's id and with a fresh GUID — while
    //     keeping the body's `<attachment>` element, which is what draws the empty box.
    //   - The Skype markup the client used to use (`<blockquote itemtype=".../Reply">`) is refused
    //     outright: "Message body content cannot contain unsupported item types".
    // Graph builds the card from the id, so the caller never restates the quoted text.
    [McpServerTool(Name = "send_chat_message", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("MUTATION: sends a message visible to everyone in the chat. Disabled unless the environment " +
                 "variable TEAMS_MCP_ALLOW_SEND=true is set for this server. `chat` is a chat id from list_chats. " +
                 "Set reply_to to a message id from read_chat_messages to answer that message as a quoted reply, " +
                 "the same card the Teams client's Reply button produces.")]
    public Task<SentMessageDto> SendChatMessage(
        [Description("Chat id, e.g. 19:...@thread.v2")] string chat,
        [Description("Message body. Plain text unless format says otherwise.")] string body,
        [Description("Body format: 'text' (default), 'markdown', or 'html'. Use markdown for anything beyond " +
                     "a single plain paragraph (Teams collapses newlines in a text body); it is converted " +
                     "server-side and renders consistently. html is a last resort.")] string? format = null,
        [Description("Id of a message in this chat to quote, from read_chat_messages. Omit to send a new " +
                     "top-level message. The quoted card is built by Teams from the id; do not restate the " +
                     "quoted text in body. Not supported in the self chat.")] string? reply_to = null,
        CancellationToken ct = default) => Run("send_chat_message",
        A("chat", chat) + A("format", format ?? "text") + A("reply_to", reply_to)
            + TeamsMcpLog.ContentArg("body", body), async () =>
    {
        RequireSendEnabled();
        RequireQuotableChat(chat, reply_to);
        var client = await graph.GetClientAsync(ct);
        var message = new ChatMessage { Body = BuildBody(body, format) };
        var created = reply_to is null
            ? await client.Chats[chat].Messages.PostAsync(message, cancellationToken: ct)
            : await client.Chats[chat].Messages.ReplyWithQuote.PostAsync(
                new Microsoft.Graph.Chats.Item.Messages.ReplyWithQuote.ReplyWithQuotePostRequestBody
                { MessageIds = [reply_to], ReplyMessage = message }, cancellationToken: ct);
        return new SentMessageDto(created?.Id, created?.CreatedDateTime, created?.WebUrl);
    });

    // A reaction is self-scoped: setting one that is already set changes nothing, and remove only
    // ever takes off the signed-in user's own reaction, so the same call twice lands on the same
    // state. Graph's setReaction takes the emoji itself (`reactionType` as unicode) and answers
    // 204, so the confirmation DTO is built here rather than read back. Measured: Graph keeps one
    // reaction per user per message — setting a different emoji MOVES the caller's reaction (the
    // Teams client's newer multi-reaction pile-on is not exposed through the public API), and the
    // 204 on a set that displaced another looks identical to one that did not.
    [McpServerTool(Name = "react_to_chat_message", UseStructuredContent = true, Destructive = false, Idempotent = true)]
    [Description("MUTATION: puts an emoji reaction on a chat message as the signed-in user; remove=true takes " +
                 "it off again. `reaction` is the emoji itself, e.g. 🤔 or ✅. The user holds one reaction per " +
                 "message through this API: setting a different emoji moves it rather than adding a second. " +
                 "Disabled unless the environment variable TEAMS_MCP_ALLOW_SEND=true is set for this server. " +
                 "`chat` is a chat id from list_chats; message ids come from read_chat_messages.")]
    public Task<ReactionDto> ReactToChatMessage(
        [Description("Chat id, e.g. 19:...@thread.v2")] string chat,
        [Description("Id of the message to react to")] string message_id,
        [Description("The reaction emoji, e.g. 🤔")] string reaction,
        [Description("Remove the signed-in user's reaction instead of setting it (default false)")] bool remove = false,
        CancellationToken ct = default) => Run("react_to_chat_message",
        A("chat", chat) + A("message_id", message_id) + A("reaction", reaction) + A("remove", remove), async () =>
    {
        RequireSendEnabled();
        var emoji = RequireReaction(reaction);
        var client = await graph.GetClientAsync(ct);
        var message = client.Chats[chat].Messages[message_id];
        if (remove)
        {
            await message.UnsetReaction.PostAsync(
                new Microsoft.Graph.Chats.Item.Messages.Item.UnsetReaction.UnsetReactionPostRequestBody
                { ReactionType = emoji }, cancellationToken: ct);
        }
        else
        {
            await message.SetReaction.PostAsync(
                new Microsoft.Graph.Chats.Item.Messages.Item.SetReaction.SetReactionPostRequestBody
                { ReactionType = emoji }, cancellationToken: ct);
        }
        return new ReactionDto(message_id, emoji, remove ? true : null);
    });

    [McpServerTool(Name = "react_to_channel_message", UseStructuredContent = true, Destructive = false, Idempotent = true)]
    [Description("MUTATION: puts an emoji reaction on a channel message as the signed-in user; remove=true " +
                 "takes it off again. `reaction` is the emoji itself, e.g. 🤔 or ✅. The user holds one reaction " +
                 "per message through this API: setting a different emoji moves it rather than adding a second. " +
                 "Disabled unless the environment variable TEAMS_MCP_ALLOW_SEND=true is set for this server. " +
                 "`team`/`channel` accept ids or display names. To react to a reply inside a thread, pass the " +
                 "thread root's id as message_id and the reply's own id as reply_id (a reply's replyToId names " +
                 "its root).")]
    public Task<ReactionDto> ReactToChannelMessage(
        [Description("Team id (GUID) or display name")] string team,
        [Description("Channel id (19:...) or display name")] string channel,
        [Description("Id of the message to react to (the thread root's id when reacting to a reply)")] string message_id,
        [Description("The reaction emoji, e.g. 🤔")] string reaction,
        [Description("Id of the reply to react to, when the target is a reply rather than the thread root")] string? reply_id = null,
        [Description("Remove the signed-in user's reaction instead of setting it (default false)")] bool remove = false,
        CancellationToken ct = default) => Run("react_to_channel_message",
        A("team", team) + A("channel", channel) + A("message_id", message_id) + A("reaction", reaction) +
        A("reply_id", reply_id) + A("remove", remove), async () =>
    {
        RequireSendEnabled();
        var emoji = RequireReaction(reaction);
        var client = await graph.GetClientAsync(ct);
        var (teamId, _) = await ResolveTeamAsync(client, team, log, ct);
        var channelId = await ResolveChannelAsync(client, teamId, channel, log, ct);
        var message = client.Teams[teamId].Channels[channelId].Messages[message_id];
        if (reply_id is null)
        {
            if (remove)
            {
                await message.UnsetReaction.PostAsync(
                    new Microsoft.Graph.Teams.Item.Channels.Item.Messages.Item.UnsetReaction.UnsetReactionPostRequestBody
                    { ReactionType = emoji }, cancellationToken: ct);
            }
            else
            {
                await message.SetReaction.PostAsync(
                    new Microsoft.Graph.Teams.Item.Channels.Item.Messages.Item.SetReaction.SetReactionPostRequestBody
                    { ReactionType = emoji }, cancellationToken: ct);
            }
        }
        else
        {
            var reply = message.Replies[reply_id];
            if (remove)
            {
                await reply.UnsetReaction.PostAsync(
                    new Microsoft.Graph.Teams.Item.Channels.Item.Messages.Item.Replies.Item.UnsetReaction.UnsetReactionPostRequestBody
                    { ReactionType = emoji }, cancellationToken: ct);
            }
            else
            {
                await reply.SetReaction.PostAsync(
                    new Microsoft.Graph.Teams.Item.Channels.Item.Messages.Item.Replies.Item.SetReaction.SetReactionPostRequestBody
                    { ReactionType = emoji }, cancellationToken: ct);
            }
        }
        return new ReactionDto(reply_id ?? message_id, emoji, remove ? true : null);
    });

    // ------------------------------------------------------------------- helpers

    private static void RequireSendEnabled()
    {
        if (!GraphContext.SendEnabled)
        {
            throw new McpException(
                "Sending is disabled. Set TEAMS_MCP_ALLOW_SEND=true in this server's environment to " +
                "opt in to posting messages and reactions other people will see. That gate also decides " +
                "whether sign-in asks for the send scopes, so a sign-in made without it needs `-- auth` again.");
        }
    }

    /// <summary>
    /// The self chat cannot carry a quoted reply through Graph, because Graph does not model it as a
    /// chat at all: `GET /chats/48:notes` answers 400 "Call made for a thread which is not a
    /// ChatThread", and `replyWithQuote` is routed as `/chats({chatThreadId})/messages/replyWithQuote`
    /// — the id it needs is the thing `48:notes` is not. Posting a plain message to it works, which
    /// is what makes the hole invisible: `replyWithQuote` answers 201 having written the body's
    /// `attachment` element and created no attachment, so the client draws an empty box above the
    /// text. Measured on v1.0 and beta alike; the same call against a `19:…@thread.v2` chat builds
    /// the `messageReference` attachment correctly. The Teams client can quote here through its own
    /// internal API, so this is a hole in Graph rather than in Teams. It fails silently at every
    /// layer a caller can see, which is why it is refused rather than left to a screenshot.
    /// </summary>
    internal static void RequireQuotableChat(string chat, string? replyTo)
    {
        if (replyTo is not null && chat.StartsWith("48:", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"The self chat ('{chat}') does not support quoted replies through Graph: the call " +
                "succeeds and the quote is dropped, leaving an empty quote box above the text. Send " +
                "without reply_to, or reply in a 19:...@thread.v2 chat.");
        }
    }

    private static string RequireReaction(string reaction)
    {
        var emoji = reaction?.Trim();
        if (string.IsNullOrEmpty(emoji))
        {
            throw new McpException("`reaction` must be the emoji to set, e.g. 🤔 or ✅.");
        }
        return emoji;
    }

    /// <summary>
    /// Builds the Graph message body from the <c>format</c> argument. Absent means plain text —
    /// markup is opt-in because Teams escapes it in a text body, so an HTML entity sent as text
    /// arrives as its literal characters. Markdown is converted here rather than passed through:
    /// Teams renders markdown typed into its own composer, but the Graph API accepts only text
    /// and html body types and shows raw markdown literally.
    /// </summary>
    internal static ItemBody BuildBody(string text, string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            return new ItemBody { ContentType = BodyType.Text, Content = text };
        }

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            return new ItemBody { ContentType = BodyType.Html, Content = Markdown.ToHtml(text) };
        }

        if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            return new ItemBody { ContentType = BodyType.Html, Content = text };
        }

        throw new McpException($"Unknown format '{format}'. Valid values are 'text', 'markdown' and 'html'.");
    }

    private static int _sequence;

    /// <summary>
    /// The next correlation id. <see cref="ToolErrors"/> allocates from the same sequence, so a
    /// failure caught either side of <see cref="Run{T}"/> is indistinguishable in the log from one
    /// caught inside it.
    /// </summary>
    internal static string NextRequest() =>
        Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Wraps every tool call: assigns the <c>req=N</c> correlation id that the Graph HTTP handler
    /// and MCP SDK events are stamped with, times the call, and records arguments and outcome.
    /// Failures log the full exception while the model sees a short message plus the req id, which
    /// is enough to find the log lines.
    /// </summary>
    internal async Task<T> Run<T>(string tool, string args, Func<Task<T>> action)
    {
        var req = NextRequest();
        var previous = TeamsMcpLog.CurrentRequest;
        TeamsMcpLog.CurrentRequest = req;
        var sw = Stopwatch.StartNew();
        log.Line(LogLevel.Information, Ev.ToolStart, tool + args);
        try
        {
            var result = await action();
            log.Line(LogLevel.Information, Ev.ToolOk,
                $"{tool} ok" + A("ms", sw.ElapsedMilliseconds) + Describe(result));
            return result;
        }
        catch (McpException e)
        {
            // Already a model-facing message (bad name, sending disabled, ...).
            log.Line(LogLevel.Warning, Ev.ToolFail,
                $"{tool} rejected" + A("ms", sw.ElapsedMilliseconds) + A("reason", e.Message));
            throw;
        }
        catch (AuthenticationRequiredException e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} auth-required" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException(
                "Sign-in expired or additional consent required. Run `dotnet run --project <teams-mcp repo>/src/TeamsMcp -- auth` again. " +
                LogRef(req));
        }
        catch (ODataError e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} graph-error" + A("ms", sw.ElapsedMilliseconds) +
                A("code", e.Error?.Code) + A("status", e.ResponseStatusCode), e);
            throw new McpException($"Graph error {e.Error?.Code}: {e.Error?.Message} {LogRef(req)}");
        }
        catch (OperationCanceledException e)
        {
            log.Line(LogLevel.Warning, Ev.ToolFail, $"{tool} cancelled" + A("ms", sw.ElapsedMilliseconds), e);
            throw;
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} unhandled" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException($"{e.GetType().Name}: {e.Message} {LogRef(req)}");
        }
        finally
        {
            TeamsMcpLog.CurrentRequest = previous;
        }
    }

    /// <summary>Points the caller at the exact log lines for this call.</summary>
    internal static string LogRef(string req) => $"(details: grep \"req={req}\" in {TeamsMcpLog.FilePath})";

    /// <summary>Summarizes a tool result for the log without dumping its content.</summary>
    internal static string Describe(object? result) => result switch
    {
        MessagesResult m =>
            A("messages", m.Messages.Count) +
            (m.HasMore is true ? A("hasMore", true) : "") +
            (m.Skipped is { } s ? A("skipped.deleted", s.Deleted ?? 0) + A("skipped.system", s.System ?? 0) : ""),
        SearchResult s => A("hits", s.Hits.Count) + A("total", s.Total) +
            (s.HasMore is true ? A("hasMore", true) : ""),
        SearchWaitResult s => A("hits", s.Hits.Count) + A("total", s.Total) +
            A("waitedSeconds", s.WaitedSeconds) + (s.TimedOut is true ? A("timedOut", true) : ""),
        SentMessageDto sent => A("messageId", sent.Id),
        ReactionDto r => A("messageId", r.MessageId) + A("reaction", r.Reaction) +
            (r.Removed is true ? A("removed", true) : ""),
        System.Collections.ICollection c => A("count", c.Count),
        _ => "",
    };

    private static async Task<List<TeamDto>> ListTeamsInternal(GraphServiceClient client, CancellationToken ct)
    {
        var results = new List<TeamDto>();
        var page = await client.Me.JoinedTeams.GetAsync(cancellationToken: ct);
        while (page is not null)
        {
            results.AddRange((page.Value ?? []).Select(t =>
                new TeamDto(t.Id, t.DisplayName, TrimDescription(t.Description, t.DisplayName))));
            if (page.OdataNextLink is null)
            {
                break;
            }
            page = await client.Me.JoinedTeams.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }
        return results;
    }

    private static async Task<(string Id, string Name)> ResolveTeamAsync(
        GraphServiceClient client, string team, ILogger log, CancellationToken ct)
    {
        if (Guid.TryParse(team, out _))
        {
            return (team, team);
        }
        var teams = await ListTeamsInternal(client, ct);
        var matches = teams.Where(t => string.Equals(t.Name, team, StringComparison.OrdinalIgnoreCase)).ToList();
        var how = "exact";
        if (matches.Count == 0)
        {
            matches = teams.Where(t => t.Name?.Contains(team, StringComparison.OrdinalIgnoreCase) == true).ToList();
            how = "substring";
        }
        if (matches is [{ Id: not null } m])
        {
            log.Line(LogLevel.Debug, Ev.Resolve,
                "team resolved" + A("input", team) + A("match", how) + A("name", m.Name) + A("id", m.Id) +
                A("candidates", teams.Count));
        }
        return matches switch
        {
            [{ Id: not null } only] => (only.Id, only.Name ?? team),
            [] => throw new McpException(
                $"No joined team matches '{team}'. Joined teams: {string.Join(", ", teams.Select(t => t.Name))}"),
            _ => throw new McpException(
                $"Team name '{team}' is ambiguous: {string.Join(", ", matches.Select(t => t.Name))}. Use the id."),
        };
    }

    private static async Task<string> ResolveChannelAsync(
        GraphServiceClient client, string teamId, string channel, ILogger log, CancellationToken ct)
    {
        if (channel.StartsWith("19:", StringComparison.Ordinal))
        {
            return channel;
        }
        var page = await client.Teams[teamId].Channels.GetAsync(cancellationToken: ct);
        var channels = page?.Value ?? [];
        var matches = channels.Where(c => string.Equals(c.DisplayName, channel, StringComparison.OrdinalIgnoreCase)).ToList();
        var how = "exact";
        if (matches.Count == 0)
        {
            matches = channels.Where(c => c.DisplayName?.Contains(channel, StringComparison.OrdinalIgnoreCase) == true).ToList();
            how = "substring";
        }
        if (matches is [{ Id: not null } m])
        {
            log.Line(LogLevel.Debug, Ev.Resolve,
                "channel resolved" + A("input", channel) + A("match", how) + A("name", m.DisplayName) +
                A("id", m.Id) + A("candidates", channels.Count));
        }
        return matches switch
        {
            [{ Id: not null } only] => only.Id,
            [] => throw new McpException(
                $"No channel matches '{channel}'. Channels: {string.Join(", ", channels.Select(c => c.DisplayName))}"),
            _ => throw new McpException(
                $"Channel name '{channel}' is ambiguous: {string.Join(", ", matches.Select(c => c.DisplayName))}. Use the id."),
        };
    }

    internal static DateTimeOffset? ParseSince(string? since)
    {
        if (string.IsNullOrWhiteSpace(since))
        {
            return null;
        }
        if (DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return ts;
        }
        throw new McpException($"Could not parse `since` value '{since}' as an ISO-8601 timestamp.");
    }

    /// <summary>Maps a Graph message to the output DTO, or returns null (and counts it) if skipped.</summary>
    internal static MessageDto? Map(ChatMessage msg, bool includeSystem, int bodyLimit, bool includeReplies, SkipCounter counts)
    {
        if (msg.DeletedDateTime is not null)
        {
            counts.Deleted++;
            return null;
        }
        var isSystem = msg.MessageType is not null && msg.MessageType != ChatMessageType.Message;
        if (isSystem && !includeSystem)
        {
            counts.System++;
            return null;
        }

        var (body, truncated) = TruncateBody(ToPlainText(msg.Body), bodyLimit);

        List<MessageDto>? replies = null;
        if (includeReplies && msg.Replies is { Count: > 0 })
        {
            replies = msg.Replies
                .OrderBy(r => r.CreatedDateTime)
                .Select(r => Map(r, includeSystem, bodyLimit, includeReplies: false, counts))
                .OfType<MessageDto>()
                .ToList();
            if (replies.Count == 0)
            {
                replies = null;
            }
        }

        var attachments = (msg.Attachments ?? [])
            .Where(a => a.Name is not null || a.ContentType is not null)
            .Select(a => new AttachmentDto(a.Name, a.ContentType))
            .ToList();

        // Keyed by the emoji (or classic type name), valued by who reacted: attribution is what
        // lets a caller tell "somebody acknowledged this" from "I already reacted to this".
        // Custom org-uploaded reactions arrive as reactionType "custom" with the name beside it.
        var reactions = new Dictionary<string, List<string>>();
        foreach (var r in msg.Reactions ?? [])
        {
            var key = r.ReactionType == "custom" ? r.DisplayName ?? "custom" : r.ReactionType;
            if (key is null)
            {
                continue;
            }
            if (!reactions.TryGetValue(key, out var who))
            {
                reactions[key] = who = [];
            }
            who.Add(r.User?.User?.DisplayName ?? r.User?.User?.Id ?? "?");
        }

        return new MessageDto(
            msg.Id,
            msg.ReplyToId,
            isSystem ? msg.MessageType?.ToString() : null, // omit the redundant default "Message"
            msg.CreatedDateTime,
            msg.From?.User?.DisplayName ?? msg.From?.Application?.DisplayName,
            body,
            truncated,
            msg.LastEditedDateTime is not null ? true : null,
            attachments.Count > 0 ? attachments : null,
            reactions.Count > 0 ? reactions : null,
            replies);
    }

    internal static (string? Body, bool? Truncated) TruncateBody(string? body, int limit)
    {
        if (body is null || limit <= 0 || body.Length <= limit)
        {
            return (body, null);
        }
        return (Cut(body, limit), true);
    }

    internal static string? TrimDescription(string? description, string? name)
    {
        if (string.IsNullOrWhiteSpace(description) ||
            string.Equals(description, name, StringComparison.OrdinalIgnoreCase))
        {
            return null; // boilerplate: repeats the name or is empty
        }
        return description.Length <= 100 ? description : Cut(description, 100) + "…";
    }

    /// <summary>
    /// Cuts at the limit, stepping back one when that would split a surrogate pair. Emoji are
    /// routine in Teams messages and half of one is an invalid character. Callers guarantee
    /// <c>s.Length &gt; limit &gt;= 1</c>.
    /// </summary>
    private static string Cut(string s, int limit) =>
        s[..(char.IsHighSurrogate(s[limit - 1]) ? limit - 1 : limit)];

    internal static string? ToPlainText(ItemBody? body)
    {
        if (body?.Content is not { Length: > 0 } content)
        {
            return null;
        }
        return body.ContentType == BodyType.Html ? HtmlToText(content) : content.Trim();
    }

    internal static string? StripHtml(string? html) => string.IsNullOrEmpty(html) ? null : HtmlToText(html);

    internal static string HtmlToText(string html)
    {
        // Links carry what an agent acts on, so keep them as "text (url)".
        var text = AnchorRegex().Replace(html, m =>
        {
            var url = m.Groups[1].Value;
            var inner = TagRegex().Replace(m.Groups[2].Value, "").Trim();
            return inner.Length == 0 || inner == url ? url : $"{inner} ({url})";
        });
        text = ImgRegex().Replace(text, "$1");   // images/emojis survive as alt text
        text = CellRegex().Replace(text, " | "); // </td>, </th>
        text = RowRegex().Replace(text, "\n");   // </tr>
        text = ListItemRegex().Replace(text, "\n- ");
        text = BlockTagRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' '); // strip &nbsp; noise
        return MultiNewlineRegex().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<a\b[^>]*\bhref=""([^""]*)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<img\b[^>]*\balt=""([^""]*)""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgRegex();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<br ?/?>|</p>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    internal sealed class SkipCounter
    {
        public int Deleted;
        public int System;

        public SkippedDto? ToDto() => Deleted == 0 && System == 0
            ? null
            : new SkippedDto(Deleted == 0 ? null : Deleted, System == 0 ? null : System);
    }
}

public sealed record TeamDto(string? Id, string? Name, string? Description);

public sealed record ChannelDto(string? Id, string? Name, string? Description, string? MembershipType);

public sealed record ChatDto(string? Id, string? Topic, string? Type, DateTimeOffset? LastUpdated, List<string?> Members);

/// <summary>Envelope for message reads: hasMore/skipped are omitted when uninteresting.</summary>
public sealed record MessagesResult(List<MessageDto> Messages, bool? HasMore, SkippedDto? Skipped);

/// <summary>
/// What a waiter returns: the <see cref="MessagesResult"/> fields plus how long the wait took,
/// whether it timed out, and where to resume. <c>TimedOut</c> is present only when the wait gave
/// up. <c>NextCursor</c> is present either way, because a timed-out wait has not lost its place.
/// </summary>
public sealed record MessagesWaitResult(
    List<MessageDto> Messages,
    bool? HasMore,
    SkippedDto? Skipped,
    int WaitedSeconds,
    bool? TimedOut,
    string? NextCursor = null);

/// <summary>One poll's answer across every watched source, before it becomes a result.</summary>
internal sealed record MergedPage(
    List<MessageDto> Messages,
    bool? HasMore,
    SkippedDto? Skipped,
    Dictionary<string, List<MessageDto>> BySource);

/// <summary>
/// Where a watch has reached in one conversation: the newest instant already delivered, plus the
/// ids delivered at exactly that instant. Graph's boundary is inclusive and several messages can
/// share a timestamp, so a bare timestamp would re-deliver the boundary message on every poll.
/// The id set makes resuming exact.
/// </summary>
internal readonly record struct Watermark(DateTimeOffset Ts, HashSet<string>? Delivered)
{
    /// <summary>Adapts the read tools' plain `since`, which has no ids and stays inclusive.</summary>
    internal static Watermark? From(DateTimeOffset? since) =>
        since is null ? null : new Watermark(since.Value, null);
}

/// <summary>
/// The opaque resume token: base64url of a small JSON envelope, one watermark per conversation.
/// A caller passes <c>nextCursor</c> back verbatim and gets exactly what it has not seen. The
/// encoding is not part of the contract, so it can grow a field without callers noticing.
/// </summary>
internal static class Cursors
{
    private const int Version = 1;

    private sealed record Entry(DateTimeOffset T, List<string>? I);

    private sealed record Envelope(int V, Dictionary<string, Entry> S);

    private static readonly JsonSerializerOptions Json =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Dictionary<string, Watermark> Decode(string? cursor)
    {
        var map = new Dictionary<string, Watermark>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return map;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(Base64Url.DecodeFromChars(cursor), Json);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new McpException(
                "Could not read `cursor`. Pass back a nextCursor from a previous call unchanged, or " +
                "omit it and use `since` to start a fresh watch.");
        }

        // A cursor from another encoding starts a fresh watch from now instead of failing: the
        // caller still wants to wait, it just cannot resume.
        if (envelope?.S is null || envelope.V != Version)
        {
            return map;
        }

        foreach (var (id, entry) in envelope.S)
        {
            map[id] = new Watermark(entry.T, entry.I is null ? null : [.. entry.I]);
        }
        return map;
    }

    internal static string Encode(IReadOnlyDictionary<string, Watermark> watermarks)
    {
        var entries = new Dictionary<string, Entry>();
        foreach (var (id, w) in watermarks)
        {
            entries[id] = new Entry(w.Ts, w.Delivered is { Count: > 0 } d ? [.. d] : null);
        }

        return Base64Url.EncodeToString(
            JsonSerializer.SerializeToUtf8Bytes(new Envelope(Version, entries), Json));
    }
}

public sealed record SkippedDto(int? Deleted, int? System);

public sealed record MessageDto(
    string? Id,
    string? ReplyToId,
    string? MessageType,
    DateTimeOffset? Created,
    string? Sender,
    string? Body,
    bool? Truncated,
    bool? Edited,
    List<AttachmentDto>? Attachments,
    // Reaction (emoji or classic type name) -> display names of who reacted. Names fall back to
    // the user id, then "?", so the list's length is always the reaction's count.
    Dictionary<string, List<string>>? Reactions,
    List<MessageDto>? Replies,
    // Set only when one call watches more than one conversation. A caller that named a single
    // chat already knows which one answered.
    string? ChatId = null);

public sealed record AttachmentDto(string? Name, string? ContentType);

/// <summary>
/// Envelope for a search. <c>total</c> is the service's estimate of everything matching, which
/// tells "these are the only three" apart from "the first three of hundreds".
/// </summary>
public sealed record SearchResult(List<SearchHitDto> Hits, bool? HasMore, int? Total);

/// <summary>What a search waiter returns: <see cref="SearchResult"/> plus how the wait ended.</summary>
public sealed record SearchWaitResult(
    List<SearchHitDto> Hits,
    bool? HasMore,
    int? Total,
    int WaitedSeconds,
    bool? TimedOut);

/// <summary>
/// One search hit. It addresses a message instead of carrying it: <c>Summary</c> is the index's
/// extract, and <c>ChatId</c> or <c>TeamId</c>+<c>ChannelId</c> names the conversation to read
/// for the body.
/// </summary>
public sealed record SearchHitDto(
    string? MessageId,
    string? ChatId,
    string? TeamId,
    string? ChannelId,
    DateTimeOffset? Created,
    string? Sender,
    string? Summary,
    bool? Truncated,
    string? WebUrl);

public sealed record SentMessageDto(string? Id, DateTimeOffset? Created, string? WebUrl);

/// <summary>
/// Confirmation of a reaction change. Graph answers 204 to setReaction/unsetReaction, so this
/// echoes what was done rather than reading anything back. <c>Removed</c> appears only when true.
/// </summary>
public sealed record ReactionDto(string? MessageId, string? Reaction, bool? Removed);
