# Teams server

`src/TeamsMcp`, command `teams-mcp`. Reads Teams conversations and sends messages as the signed-in
user, through Microsoft Graph.

Everything here is on top of the shared conventions in [tool-contract.md](tool-contract.md) and the
sign-in design in [authentication.md](authentication.md).

## The Graph client

`GraphServiceClient` from the official SDK (Kiota-generated), built in `GraphContext.GetClientAsync`
from `GraphClientFactory.CreateDefaultHandlers()` with `GraphLoggingHandler` **appended last**, so
it is innermost and sees each retry attempt individually rather than only the outcome the retry
handler settled on.

The authentication provider is an `AzureIdentityAuthenticationProvider` with `isCaeEnabled: false`,
which must match the `auth` flow or the persisted cache silently misses.

## Reading messages

Channel messages and chat messages come back as the same `ChatMessageCollectionResponse` with the
same `OdataNextLink`, so only the *first* request differs. That is what the pager delegate pair is
for:

```csharp
private delegate Task<ChatMessageCollectionResponse?> FirstPage(CancellationToken ct);
private delegate Task<ChatMessageCollectionResponse?> NextPage(string url, CancellationToken ct);
```

`ChannelPager` and `ChatPager` each produce one pair, and `PageMessagesAsync` walks it newest-first,
mapping and skip-counting as it goes. It stops at `limit` (setting `hasMore`) or at the first
message older than the floor, after which nothing newer can appear.

Both the read tools and the waiters go through `PageMessagesAsync`, which is why they return the
same shape.

`Map` is where the output conventions land: deleted messages are counted and dropped, system
messages are counted and dropped unless `include_system`, `messageType` is emitted only when it is
not the default `Message`, replies are nested and ordered oldest-first when `include_replies`, and
reactions collapse to a `{type: count}` dictionary.

## The waiters, and why they need a cursor

`wait_for_channel_messages` and `wait_for_chat_messages` poll `PageMessagesAsync` until something
newer than a watermark arrives, or the clock runs out. `PollAsync` is the shared loop; a timeout
returns the last probe's result with `timedOut: true`, so what the probe learned along the way (a
skip count) survives.

**Graph's `since` boundary is inclusive, and several messages can share a timestamp.** A caller
resuming from a timestamp it saw in a result therefore gets the boundary message again on every
poll, forever. That is the problem the cursor solves.

```csharp
internal readonly record struct Watermark(DateTimeOffset Ts, HashSet<string>? Delivered);
```

A watermark is the newest instant already delivered **plus the ids delivered at exactly that
instant**. `PageMessagesAsync` skips a message whose timestamp equals the floor and whose id is in
that set. `Advance` moves the watermark to the newest delivered message and records every id at
that instant; when nothing was delivered it stays put.

`Cursors.Encode`/`Decode` wrap a dictionary of watermarks — one per conversation — as base64url of a
small versioned JSON envelope. **The encoding is not part of the contract**, so it can grow a field
without callers noticing. A cursor from another version starts a fresh watch from now rather than
failing: the caller still wants to wait, it just cannot resume. A cursor that is not decodable at
all is an `McpException`, because that is a caller bug rather than a version skew.

Every wait returns `nextCursor`, **timeouts included** — a timed-out wait has not lost its place.

### Watching several chats at once

`wait_for_chat_messages` takes `chat` or `chats` (up to `MaxWaitChats` = 20), deduplicated in the
order given by `ChatTargets`. Each target is polled concurrently within one probe and the pages are
merged newest-first by `MergePages`.

The merge semantics are the subtle part:

- **`limit` applies to the merged list**, so a busy conversation cannot starve a quiet one. A source
  whose messages were all trimmed out delivered nothing, its watermark stays put, and it drains on
  the next call.
- **The cursor advances only over messages actually returned**, never over what a probe merely saw.
  That is what makes trimming safe.
- **One lossy case remains**: a source that delivered *part* of a burst. Its watermark moves to the
  newest message delivered and the rest of that burst is skipped. `hasMore: true` says so, and the
  fix is a higher `limit`.

Why one call watches many conversations at all: a caller that can only block once — an agent
harness, typically — otherwise has to choose one conversation to watch.

### Limits

| Constant | Value | In |
| --- | --- | --- |
| `MinPollSeconds` | 5 | `TeamsTools` |
| `MinSearchPollSeconds` | 20 | `TeamsTools` — a search poll costs a real query and the index moves slowly |
| `MaxWaitChats` | 20 | Every target costs a Graph call per poll |
| timeout clamp | 1–3600 s | `PollAsync` |
| poll interval clamp | floor–600 s | `PollAsync` |

## Search: one mechanism behind four tools

`search_messages`, `list_mentions`, `wait_for_mentions` and `wait_for_any_message` are all the
Microsoft Search API over `chatMessage` with a different KQL prefix.

That API is the only delegated surface reaching every chat *and* every joined team's channels in one
request. There is no "all my messages" endpoint, and walking each conversation is exactly the
unbounded scan the paging rules forbid. The trade is freshness and detail — which is why the server
instructions tell the model not to conclude "nothing was said" from a search that came back empty
seconds after the fact.

Three service behaviours are load-bearing, and none are guessable from the SDK's types.

**A hit is not a `ChatMessage`.** Graph answers `"@odata.type": "microsoft.graph.chatMessage"`
*without* the leading `#` the generated discriminator expects, so the SDK falls back to base
`Entity` and every property lands in `AdditionalData` as untyped nodes. Everything in `Search.cs`
reads that bag, tolerating a value arriving as an `UntypedNode`, a boxed primitive or a raw
`JsonElement`. **A mapper written against the typed model compiles, runs, and returns nothing but
nulls** — which is the failure this comment exists to prevent a second time.

**There is no body**, with or without an explicit `fields` list — only the index's `summary`. So a
hit *addresses* a message (`chatId`, or `teamId` + `channelId`, plus `webUrl`) and a read tool
fetches the text. `Map` returns only the address a follow-up read would actually open: a channel hit
repeats its channel id as `chatId`, and a 1:1 chat hit carries a `channelIdentity` naming the
personal-chat substrate, so both are filtered out.

**`sent>` is day-granular and excludes the day it names.** `sent>2026-07-28` returns nothing from the
28th. `SearchQueries.Build` therefore backs the term off by one day and `IsAtOrAfter` applies the
exact timestamp client-side. **The KQL term is an optimization, not the filter.**

Two smaller details:

- The sender arrives as the Exchange substrate's `from.emailAddress.name` rather than the
  `identitySet` the message APIs return. `Sender` reads both shapes.
- A hit with an unreadable timestamp is kept when no `since` was asked for and dropped when one was:
  a waiter that accepted it would report an arrival it has no evidence for.

Paging is `From`/`Size` at 25 per request, capped at 8 requests. Hitting the cap sets `hasMore` and
logs a Warning rather than passing an incomplete answer off as complete.

`total` is the service's estimate of everything matching, which tells "these are the only three"
apart from "the first three of hundreds". It is **dropped on a timeout**: it counts what the
day-granular scope matched, which is more than the caller waited for, so returning it alongside zero
hits would read as a contradiction.

## Sending

`send_channel_message` and `send_chat_message` call `RequireSendEnabled()` first. In this server the
gate is checked twice over — at the call and at sign-in — because it also decides whether the send
scopes are requested at all. See [authentication.md](authentication.md#teams-the-scope-list-follows-the-send-gate);
the refusal message says so explicitly, because "the gate is on but it still refuses" is otherwise a
confusing state.

`format` defaults to text. Markup is opt-in because Teams escapes it in a text body, so an HTML
entity sent as text arrives as its literal characters. An unknown value is an `McpException`
rather than a silent fallback.

`markdown` is the format the tool descriptions steer toward for anything with structure, and it
is converted to HTML server-side (`Markdown.ToHtml`) rather than passed through — the Graph API
accepts only text and html body types and renders raw markdown literally. Converting server-side
is also the design point: markdown constrains a caller to a few constructs that all render well,
where hand-written HTML has enough flexibility to go wrong in ways only a screenshot catches.
Three rendering facts, measured in both the desktop and web clients, shape the output:

- **Newlines in a text body collapse** — including blank lines, so a multi-paragraph plain-text
  message arrives as one dense block. Plain text is only fit for a single paragraph.
- **`<p>` renders with no margin**, so adjacent paragraphs touch. The converter therefore merges
  consecutive paragraphs into one `<p>` joined by `<br/><br/>` — a literal blank line on every
  client, independent of client CSS — and puts an `&nbsp;` spacer paragraph above each heading
  (the idiom the Teams composer itself emits for a blank line), except at the start or directly
  under another heading.
- **Lists, code blocks and blockquotes carry their own margins** and get no extra spacing.

Headings map `#`–`###` to `h1`–`h3` (all render at modest chat-appropriate sizes); deeper levels
fall back to a bold paragraph, which is also roughly what `h4`+ looks like anyway. Emphasis uses
the same word-boundary regexes as the read-side converters, so `snake_case` identifiers survive;
URLs and code spans are shielded before the emphasis pass for the same reason. Input text is
HTML-escaped first, so markup in a markdown body arrives as literal characters — the only tags
sent are the ones the converter emits.

## Tool inventory

| Tool | Notes |
| --- | --- |
| `list_teams` | Joined teams |
| `list_channels` | `team` by id or name |
| `list_chats` | Filters by `member` or `topic` client-side; scan capped at 500 |
| `read_channel_messages` | `include_replies` expands the thread |
| `read_chat_messages` | |
| `wait_for_channel_messages` | Cursor-based, task-capable |
| `wait_for_chat_messages` | Up to 20 chats in one call, cursor-based, task-capable |
| `search_messages` | Search index |
| `list_mentions` | `IsMentioned:true` over the same index |
| `wait_for_mentions` | Polls `list_mentions` |
| `wait_for_any_message` | Polls `search_messages` |
| `send_channel_message` | `TEAMS_MCP_ALLOW_SEND=true` |
| `send_chat_message` | `TEAMS_MCP_ALLOW_SEND=true` |

## Scopes

`GraphContext.ReadScopes`: `User.Read`, `Team.ReadBasic.All`, `Channel.ReadBasic.All`, `Chat.Read`,
`ChannelMessage.Read.All` (admin consent). `SendScopes`: `ChannelMessage.Send`, `ChatMessage.Send`,
requested only behind the gate.

Adding a capability means adding to `ReadScopes` **and** the app registration's delegated
permissions, then re-running `-- auth`.
