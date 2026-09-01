# Teams server

`src/TeamsMcp`, command `teams-mcp`. Reads Teams conversations and sends messages as the signed-in
user, through Microsoft Graph. Shared conventions: [tool-contract.md](tool-contract.md). Sign-in:
[authentication.md](authentication.md).

## The Graph client

`GraphContext.GetClientAsync` builds a `GraphServiceClient` from
`GraphClientFactory.CreateDefaultHandlers()` with `GraphLoggingHandler` **appended last** so it sits
innermost and sees each retry attempt, not just the outcome the retry handler settled on.

The authentication provider is an `AzureIdentityAuthenticationProvider` with `isCaeEnabled: false`,
which must match the `auth` flow or the persisted cache silently misses.

## Reading messages

Channel and chat messages come back as the same `ChatMessageCollectionResponse` with the same
`OdataNextLink`, so only the *first* request differs. Hence the pager delegate pair:

```csharp
private delegate Task<ChatMessageCollectionResponse?> FirstPage(CancellationToken ct);
private delegate Task<ChatMessageCollectionResponse?> NextPage(string url, CancellationToken ct);
```

`ChannelPager` and `ChatPager` each produce one pair. `PageMessagesAsync` walks it newest-first,
mapping and skip-counting, and stops at `limit` (setting `hasMore`) or at the first message older
than the floor, past which nothing newer can appear. The read tools and the waiters both go through
it, so they return the same shape.

`MapMessage` applies the output conventions: deleted messages counted and dropped, system messages
too unless `include_system`, `messageType` emitted only when it is not the default `Message`, replies
nested oldest-first when `include_replies`. Reactions collapse to a `{reaction: [who]}` dictionary
keyed by the emoji (or a classic type name like `like`; a custom org-uploaded reaction by its name)
and valued by the reactors' display names, falling back to id then `?`, so the list's length is the
count. Attribution separates "somebody acknowledged this" from "I already reacted to this".

## Downloading images

`download_message_images` handles the two ways a message carries an image.

**Inline (pasted) images are hosted content**, referenced from the body HTML as
`.../messages/{id}/hostedContents/{id}/$value` and readable with the `Chat.Read` /
`ChannelMessage.Read.All` the read tools already use, so no new consent. The `hostedContents`
*listing* answers `contentType` and `contentBytes` as null (only `/$value` carries the payload), and
nothing else names the image either, so the bytes are sniffed (`Images.Sniff`, magic numbers) and the
file named `{message_id}-{n}` plus the sniffed extension.

**Attached files are OneDrive/SharePoint references** (`contentType: "reference"`, a `contentUrl`
into the sender's drive), fetched through `/shares/{encoded contentUrl}/driveItem/content`;
`Images.EncodeShareUrl` is Graph's unpadded-base64url `u!` encoding. That needs a Files scope this
server does not request. Such an attachment failing is reported per file, `error` in place of
`path`, never by failing the call: the hosted-content images beside it still download. Non-image
attachments (cards, quote references, other files) are counted in `skippedAttachments` rather than
dropped.

The tool never overwrites: a collision in the caller's directory gets a numbered stem (`pic-2.png`).
The extension comes from the bytes, not the attachment's name, so a `.png` that sniffs as JPEG is
saved `.jpg` and opens.

Chat message, channel root and channel reply are three Kiota request-builder types with no common
interface, so the tool collapses them into delegates before the shared download loop.

## The waiters, and why they need a cursor

`wait_for_channel_messages` and `wait_for_chat_messages` poll `PageMessagesAsync` until something
newer than a watermark arrives or the clock runs out. `PollAsync` is the shared loop; a timeout
returns the last probe's result with `timedOut: true`, so its skip count survives.

Graph's `since` boundary is inclusive and several messages can share a timestamp, so a caller
resuming from a timestamp it saw in a result gets the boundary message again on every poll, forever.

```csharp
internal readonly record struct Watermark(DateTimeOffset Ts, HashSet<string>? Delivered);
```

A watermark is the newest instant already delivered **plus the ids delivered at exactly that
instant**. `PageMessagesAsync` skips a message whose timestamp equals the floor and whose id is in
that set. `Advance` moves the watermark to the newest delivered message and records every id at that
instant; when nothing was delivered it stays put.

`Cursors.Encode`/`Decode` wrap one watermark per conversation as base64url of a small versioned JSON
envelope. **The encoding is not part of the contract**, so it can grow a field without callers
noticing. A cursor from another version starts a fresh watch from now rather than failing; one
that will not decode at all is an `McpException`, a caller bug and not version skew.

Every wait returns `nextCursor`, timeouts included.

### Watching several chats at once

A caller that can only block once, typically an agent harness, would otherwise have to pick one
conversation. `wait_for_chat_messages` takes `chat` or `chats` (up to `MaxWaitChats` = 20),
deduplicated in the order given by `ChatTargets`, polls each concurrently within one probe, and
merges the pages newest-first with `MergePages`. The merge semantics:

- **`limit` applies to the merged list**, so a busy conversation cannot starve a quiet one. A source
  whose messages were all trimmed out delivered nothing, keeps its watermark, and drains next call.
- **The cursor advances only over messages actually returned**, never over what a probe merely saw.
  That is what makes trimming safe.
- **One lossy case remains**: a source that delivered *part* of a burst. Its watermark moves to the
  newest message delivered and the rest of the burst is skipped. `hasMore: true` says so; the fix is
  a higher `limit`.

### Limits

| Constant | Value | In |
| --- | --- | --- |
| `MinPollSeconds` | 5 | `TeamsTools` |
| `MinSearchPollSeconds` | 20 | `TeamsTools`; a search poll costs a real query, and the index is slow |
| `MaxWaitChats` | 20 | Every target costs a Graph call per poll |
| timeout clamp | 1–3600 s | `PollAsync` |
| poll interval clamp | floor–600 s | `PollAsync` |

## Search: one mechanism behind four tools

`search_messages`, `list_mentions`, `wait_for_mentions` and `wait_for_any_message` are all the
Microsoft Search API over `chatMessage` with a different KQL prefix. It is the only delegated surface
reaching every chat *and* every joined team's channels in one request; there is no "all my messages"
endpoint, and walking each conversation is the unbounded scan the paging rules forbid. The trade is
freshness and detail, so the server instructions tell the model not to conclude "nothing was said"
from a search that came back empty seconds after the fact.

None of the three behaviours below is guessable from the SDK's types.

**A hit is not a `ChatMessage`.** Graph answers `"@odata.type": "microsoft.graph.chatMessage"`
*without* the leading `#` the generated discriminator expects, so the SDK falls back to base `Entity`
and every property lands in `AdditionalData` as untyped nodes. Everything in `Search.cs` reads that
bag, tolerating a value arriving as an `UntypedNode`, a boxed primitive or a raw `JsonElement`.
**A mapper written against the typed model compiles, runs, and returns nothing but nulls.**

**There is no body**, with or without an explicit `fields` list, only the index's `summary`. A hit
*addresses* a message (`chatId`, or `teamId` + `channelId`, plus `webUrl`) and a read tool fetches
the text. `MapHit` returns only the address a follow-up read would open: a channel hit repeats its
channel id as `chatId`, and a 1:1 chat hit carries a `channelIdentity` naming the personal-chat
substrate, so both are filtered out.

**`sent>` is day-granular and excludes the day it names.** `sent>2026-07-28` returns nothing from the
28th. `Search.Build` backs the term off by one day and `IsAtOrAfter` applies the exact
timestamp client-side. The KQL term is an optimization, not the filter.

Two smaller details:

- The sender arrives as the Exchange substrate's `from.emailAddress.name`, not the `identitySet` the
  message APIs return. `Sender` reads both shapes.
- A hit with an unreadable timestamp is kept when no `since` was asked for and dropped when one was:
  a waiter that accepted it would report an arrival it has no evidence for.

Paging is `From`/`Size` at 25 per request, capped at 8 requests; hitting the cap sets `hasMore` and
logs a Warning. `total` is the service's estimate of everything matching, which separates "these are
the only three" from "the first three of hundreds". It is **dropped on a timeout**, where it counts
what the day-granular scope matched rather than what the caller waited for, and would contradict the
zero hits beside it.

## Sending

`send_channel_message` and `send_chat_message` call `RequireSendEnabled()` first. The gate is checked
twice in this server, at the call and at sign-in, because it also decides whether the send scopes are
requested at all (see
[authentication.md](authentication.md#teams-the-scope-list-follows-the-send-gate)). The refusal
message says so, since "the gate is on but it still refuses" is otherwise a confusing state.

The content parameter is `body`, matching the read tools (`body` in a message DTO, `body_limit` on
every read). It was `text` until a caller that had just read a conversation supplied `body`, was
rejected, and re-sent the whole message. The schema was the only place the word `text` appeared, and
`format: "text"` is a different thing again.

`format` defaults to text, and markup is opt-in because Teams escapes it in a text body, so an HTML
entity sent as text arrives as its literal characters. An unknown value is an `McpException`, not a
silent fallback.

The tool descriptions steer toward `markdown` for anything with structure. `Markdown.ToHtml` converts
it server-side: the Graph API accepts only text and html body types and renders raw markdown
literally, and markdown limits a caller to constructs that all render well, where hand-written HTML
goes wrong in ways only a screenshot catches. Three rendering facts, measured in both the desktop
and web clients, shape the output:

- **Newlines in a text body collapse**, blank lines included, so a multi-paragraph plain-text message
  arrives as one dense block. Plain text is fit only for a single paragraph.
- **`<p>` renders with no margin**, so adjacent paragraphs touch. The converter merges consecutive
  paragraphs into one `<p>` joined by `<br/><br/>`, a literal blank line on every client whatever its
  CSS, and puts an `&nbsp;` spacer paragraph above each heading (the idiom the Teams composer itself
  emits for a blank line), except at the start or directly under another heading.
- **Lists, code blocks and blockquotes carry their own margins** and get no extra spacing.

Headings map `#`–`###` to `h1`–`h3`, all rendering at modest chat sizes; deeper levels fall back to a
bold paragraph, roughly what `h4`+ looks like anyway. Emphasis uses the read-side converters'
word-boundary regexes so `snake_case` identifiers survive, and URLs and code spans are shielded
before the emphasis pass. Input text is HTML-escaped first, so markup in a markdown body arrives as
literal characters and the only tags sent are the converter's.

### Replying

`reply_to` means a different thing on each send tool. A channel has one-level threading, so
`send_channel_message` posts into the thread root's `replies` collection. A chat has none: its
"Reply" is a quote card, a `messageReference` attachment anchored by an `<attachment>` element in the
body, which `send_chat_message` produces through Graph's `replyWithQuote` action. Both halves are
required: a body whose `<attachment>` element names an attachment missing from the array renders as
an **empty box above the text**, the signature of every failure below.

Measured 2026-08-14 against the live service. None of it is visible from a return code.

- **The self chat is not a chat, and cannot be replied to.** `GET /chats/48:notes` answers
  `400 BadRequest: Call made for a thread which is not a ChatThread`, and `replyWithQuote` routes as
  `/chats({chatThreadId})/messages/replyWithQuote`, needing exactly the id `48:notes` is not. Posting
  a plain message works, which hides the hole: `replyWithQuote` answers `201 Created` having written
  the body's `<attachment>` element and created no attachment, so the quote renders as an empty box.
  Identical on v1.0 and beta; the same call against a `19:…@thread.v2` chat builds the attachment
  correctly. The `/me/chats/{id}/…` and `/users/{id}/chats/{id}/…` routes both 404 with *"Request
  path is not supported"*. `RequireQuotableChat` refuses `reply_to` for a `48:` chat rather than
  posting the broken message, since nothing downstream would report a problem.
- **The attachment cannot be composed by hand.** Building the `messageReference` exactly as the Teams
  client writes it and posting it as an ordinary message fails in any chat: Graph strips the
  attachment and keeps the body's element. Tried with the quoted message's own id and with a fresh
  GUID, since the client's non-GUID id suggested id validation was the cause. It was not.
- **The Skype markup is refused outright.** `<blockquote itemscope itemtype="http://schema.skype.com/Reply">`,
  with or without its inner `itemprop` elements, fails with `Message body content cannot contain
  unsupported item types`.

The Teams client *can* quote in the self chat through its own internal API, so the hole is in Graph
and this server cannot route around it: a client-authored reply in `48:notes` and a Graph-authored
one in a real chat read back with the same attachment.

## Reactions

`react_to_chat_message` and `react_to_channel_message` are Graph's `setReaction`/`unsetReaction`
behind the same `RequireSendEnabled()` gate as the sends, since a reaction is visible to everyone in
the conversation. The service facts that shape them:

- **`reactionType` is the emoji itself**, passed as unicode (`{"reactionType": "🤔"}`) and not an
  enum: any emoji Teams can react with works, alongside the classic names. Graph answers
  `204 No Content` both ways, so the result DTO echoes what was done rather than reading back.
- **One reaction per user per message: a set moves, never stacks.** Measured 2026-07-31 against the
  live service: setting a second emoji as the same user displaces the first, and the 204 looks
  identical either way, so the replacement shows only on a read-back. The Teams *client* can stack
  several reactions from one user; that newer feature is not exposed through public Graph. The tool
  descriptions say so, because a model that reacts 🤔 and later ✅ needs to know it moved its
  reaction rather than added one.
- **The delegated permissions are the send scopes already requested**: `ChatMessage.Send` covers chat
  reactions, `ChannelMessage.Send` covers channel ones, so reacting adds no scope and no re-consent.
- **A channel reply is addressed through its thread root** (`messages/{root}/replies/{reply}`), so
  the channel tool takes `reply_id` alongside `message_id` instead of a reply id alone. Graph has no
  reply-by-id endpoint without the root.

`remove=true` unsets, and only ever the signed-in user's own reaction, which makes the tools
idempotent (`Idempotent = true` where the sends are false).

## Tool inventory

| Tool | Notes |
| --- | --- |
| `list_teams` | Joined teams |
| `list_channels` | `team` by id or name |
| `list_chats` | Filters by `member` or `topic` client-side; scan capped at 500; leads with the self chat |
| `get_current_user` | One `/me` call, cached for the process; carries the self-chat id |
| `read_channel_messages` | `include_replies` expands the thread |
| `read_chat_messages` | `chat` by id, topic, person or `self` |
| `download_message_images` | Hosted content + OneDrive image references, saved to a local directory; same `chat` forms |
| `wait_for_channel_messages` | Cursor-based, task-capable |
| `wait_for_chat_messages` | Up to 20 chats in one call, same `chat` forms, cursor-based, task-capable |
| `search_messages` | Search index |
| `list_mentions` | `IsMentioned:true` over the same index |
| `wait_for_mentions` | Polls `list_mentions` |
| `wait_for_any_message` | Polls `search_messages` |
| `send_channel_message` | `TEAMS_MCP_ALLOW_SEND=true` |
| `send_chat_message` | `TEAMS_MCP_ALLOW_SEND=true`; `chat` by id, topic, person or `self` |
| `react_to_chat_message` | `TEAMS_MCP_ALLOW_SEND=true`; emoji via setReaction/unsetReaction; same `chat` forms |
| `react_to_channel_message` | Same gate; `reply_id` reaches a reply through its thread root |

## Scopes

`GraphContext.ReadScopes`: `User.Read`, `Team.ReadBasic.All`, `Channel.ReadBasic.All`, `Chat.Read`,
`ChannelMessage.Read.All` (admin consent). `SendScopes`: `ChannelMessage.Send`, `ChatMessage.Send`,
requested only behind the gate.

Adding a capability means adding to `ReadScopes` **and** the app registration's delegated
permissions, then re-running `-- auth`.

The known gap: `download_message_images` reaches an attached OneDrive/SharePoint file through
`/shares/{id}/driveItem`, which wants a Files scope (e.g. `Files.Read.All`) that `ReadScopes` omits.
Inline hosted content, the common case, needs nothing beyond the message scopes, and widening every
deployment's consent for the rare attached-file case is the wrong trade. The tool reports the
missing scope per file instead of failing; adding the scope is the re-consent dance above.
