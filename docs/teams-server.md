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
reactions collapse to a `{reaction: [who]}` dictionary — keyed by the emoji (or a classic type name
like `like`; a custom org-uploaded reaction is keyed by its name), valued by the reactors' display
names falling back to id then `?`, so the list's length is always the count. Attribution is what
lets a caller tell "somebody acknowledged this" from "I already reacted to this".

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

The content parameter is `body`, the same word the read tools use for the same thing (`body` in a
message DTO, `body_limit` on every read). It was `text` until a caller that had just read a
conversation supplied `body`, was rejected, and re-sent the whole message — the schema was the only
place the word `text` appeared, and `format: "text"` means something else again.

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

### Replying

`reply_to` on either send tool answers an existing message, and it means a different thing in each
because Teams' two conversation kinds are different things. A channel has real one-level threading,
so `send_channel_message` posts into the thread root's `replies` collection and the reply lands
inside the thread. A chat has no threading at all: its "Reply" is a quote card, carried by a
`messageReference` attachment that an `<attachment>` element in the body anchors, and
`send_chat_message` produces one through Graph's `replyWithQuote` action. Both halves of that pair
are required — a body whose `<attachment>` element names an attachment that is not in the array
renders as an **empty box above the text**, which is the signature of every failed attempt below.

Measured 2026-08-14 against the live service, and none of it is visible from a return code:

- **The self chat is not a chat, and so cannot be replied to.** `GET /chats/48:notes` answers
  `400 BadRequest: Call made for a thread which is not a ChatThread`, and `replyWithQuote` routes as
  `/chats({chatThreadId})/messages/replyWithQuote` — the id it requires is exactly what `48:notes` is
  not. Posting a plain message to it works, which is what hides the hole: `replyWithQuote` answers
  `201 Created` having written the body's `<attachment>` element and created no attachment, so the
  quote renders as an empty box. Identical on v1.0 and beta; the same call against a
  `19:…@thread.v2` chat builds the attachment correctly. The `/me/chats/{id}/…` and
  `/users/{id}/chats/{id}/…` routes are not alternatives — both 404 with *"Request path is not
  supported"*. `RequireQuotableChat` therefore refuses `reply_to` for a `48:` chat rather than
  posting the broken message: nothing downstream would report a problem, so the alternative is
  discovering it in a screenshot.
- **The attachment cannot be composed by hand.** Building the `messageReference` exactly as the
  Teams client writes it and posting it as an ordinary message does not work in any chat: Graph
  strips the attachment and keeps the body's element — tried with the quoted message's own id and
  with a fresh GUID, since the client's non-GUID id suggested id validation was the cause. It was
  not.
- **The Skype markup is refused outright.** `<blockquote itemscope itemtype="http://schema.skype.com/Reply">`,
  with or without its inner `itemprop` elements, fails with `Message body content cannot contain
  unsupported item types`.

The Teams client *can* quote in the self chat, through its own internal API. That is a hole in
Graph rather than in Teams, and it is not one this server can route around — a client-authored
reply in `48:notes` and a Graph-authored one in a real chat read back with the same attachment.

## Reactions

`react_to_chat_message` and `react_to_channel_message` are Graph's `setReaction`/`unsetReaction`
actions behind the same `RequireSendEnabled()` gate as the sends — a reaction is visible to
everyone in the conversation. Three service facts shape them:

- **`reactionType` is the emoji itself**, passed as unicode (`{"reactionType": "🤔"}`), not an
  enum: any emoji Teams can react with works, alongside the classic names. Graph answers
  `204 No Content` both ways, so the result DTO echoes what was done rather than reading back.
- **One reaction per user per message: a set moves, never stacks.** Measured 2026-07-31 against
  the live service: setting a second emoji as the same user displaces the first, and the 204 looks
  identical either way — the replacement is only visible on a read-back. The Teams *client* can
  pile several reactions from one user onto a message, but that newer multi-reaction feature is
  not exposed through public Graph. The tool descriptions say so, because a model that reacts 🤔
  and later ✅ needs to know it moved its reaction rather than added one (which happens to be the
  right behaviour for an acknowledge-then-done workflow).
- **The delegated permissions are the send scopes already requested** — `ChatMessage.Send` covers
  chat reactions and `ChannelMessage.Send` covers channel ones — so reacting adds no scope and no
  re-consent.
- **A channel reply is addressed through its thread root** (`messages/{root}/replies/{reply}`),
  which is why the channel tool takes `reply_id` alongside `message_id` instead of accepting a
  reply id alone: Graph has no reply-by-id endpoint without the root.

`remove=true` unsets, and only ever the signed-in user's own reaction, which is what makes the
tools idempotent (`Idempotent = true` where the sends are false): the same call twice lands on the
same state.

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
| `react_to_chat_message` | `TEAMS_MCP_ALLOW_SEND=true`; emoji via setReaction/unsetReaction |
| `react_to_channel_message` | Same gate; `reply_id` reaches a reply through its thread root |

## Scopes

`GraphContext.ReadScopes`: `User.Read`, `Team.ReadBasic.All`, `Channel.ReadBasic.All`, `Chat.Read`,
`ChannelMessage.Read.All` (admin consent). `SendScopes`: `ChannelMessage.Send`, `ChatMessage.Send`,
requested only behind the gate.

Adding a capability means adding to `ReadScopes` **and** the app registration's delegated
permissions, then re-running `-- auth`.
