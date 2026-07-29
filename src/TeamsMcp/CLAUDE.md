# CLAUDE.md — TeamsMcp

Teams-server specifics. The repository-wide rules are in the root `CLAUDE.md` and still apply;
`docs/teams-server.md` and `docs/authentication.md` are the long form of what follows.

## Architecture constraints

**Teams' scope list follows the send gate, and consent is not a token.** `GraphContext.Scopes` is
computed — `ScopesFor(SendEnabled)`, read scopes plus the two send scopes only when
`TEAMS_MCP_ALLOW_SEND=true` — so a read-only deployment never asks anyone to consent to posting as
the signed-in user. Three things about that are measured rather than assumed, and a change here
should keep them true:
- **Narrowing the request narrows consent, not the token.** Entra returns every scope the user has
  already granted the app registration: a five-scope read-only request against a consented tenant
  came back carrying `ChannelMessage.Send`, `ChatMessage.Send` and several scopes this server never
  asks for. Least privilege here is about what a *first* sign-in grants; it cannot take a permission
  back.
- **The gate can therefore outrun consent**, which is the failure mode the reduction introduces:
  `auth` and the server each compute the scope list when they run, so enabling the gate afterwards
  asks for scopes that sign-in may never have consented to. `ScopeConsent` writes the granted set to
  `auth-scopes.json` beside the authentication record (which carries identity but not scopes), and
  `GetClientAsync` compares before acquiring — logging `auth.mismatch`, then either succeeding
  (consent was already in place, and the record is corrected) or failing with an `McpException` that
  names the missing scopes and asks for `-- auth`. **Missing or unreadable means unknown, never
  empty**: a sign-in from before the file existed consented to everything, and a warning on every
  startup of a server that works is worse than no warning.
- **The recorded set is read off the token's `scp` claim**, not off the request, because those
  differ. `ScopeConsent.FromToken` returns null for anything it cannot parse, and the token itself
  never leaves that method.

**Teams search is one mechanism behind four tools, and its hits are untyped.** `search_messages`,
`list_mentions`, `wait_for_mentions` and `wait_for_any_message` are all the Microsoft Search API
over `chatMessage` with a different KQL prefix. That API is the only delegated surface reaching
every chat *and* every joined team's channels in one request — there is no "all my messages"
endpoint, and walking each conversation is the unbounded scan the paging rules forbid. Three
service behaviours are load-bearing and none are guessable from the SDK's types:
- **A hit is not a `ChatMessage`.** Graph answers `"@odata.type": "microsoft.graph.chatMessage"`
  without the leading `#`, which does not match the generated discriminator, so the SDK falls back
  to base `Entity` and every property lands in `AdditionalData` as untyped nodes. `Search.cs` reads
  that bag; a mapper written against the typed model compiles, runs, and returns nothing but nulls.
- **There is no body**, with or without an explicit `fields` list — only the index's `summary`.
  Hits therefore address a message (`chatId`, or `teamId`+`channelId`) for a read tool to fetch.
- **`sent>` is day-granular and excludes the day it names**, so `since` is pushed down as a scope
  backed off by one day and then applied exactly client-side.
