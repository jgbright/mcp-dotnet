# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Two .NET 10 console apps, each running as an **MCP stdio server**, both using the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK.

The SDK is pinned at **2.0.0**, which speaks the **2026-07-28** protocol revision and negotiates
down to 2025-11-25 (and 2024-11-05) for older clients — verified by hand: an old-style `initialize`
still completes against both servers. Most of that revision is about making HTTP stateless and so
never reaches a stdio server, but four things do, all covered under "Tool conventions" below:
`tools/list` now carries caching hints; tool results can carry an output schema; the Tasks
extension gives long-running tools somewhere to live; and Roots/Sampling/Logging are deprecated in
favour of what these servers already do (pass paths as tool arguments; log to stderr). Server-
initiated requests are deprecated in favour of Multi Round-Trip Requests; neither server has ever
used them, so there is nothing to migrate.

`src/TeamsMcp/` (Microsoft Graph) and `src/AzureDevOpsMcp/` (Azure DevOps REST), a test project
each under `tests/`, all four in `McpServers.slnx`. The filenames say what they hold.

**`docs/` is the long form of this file.** The rules below are normative and terse; `docs/` explains
the designs they come from — `architecture.md`, `authentication.md`, `tool-contract.md`,
`observability.md`, one document per server, and `distribution.md`. Read the relevant one before
a change big enough that "why is it like this" matters; the rules here are enough for a change
that only has to not break anything.

**Constraints that bind one server only live in that server's own `CLAUDE.md`** —
`src/TeamsMcp/CLAUDE.md` (the scope list and the send gate's effect on consent; Microsoft Search
hits being untyped) and `src/AzureDevOpsMcp/CLAUDE.md` (why there is no Azure DevOps client SDK;
what the write tools must reach). Those load when the work is under that directory. Everything
below this line binds both servers.

**The two servers are independent processes and share no project.** The conventions below are
shared and the code implementing them is deliberately duplicated (`Logging.cs` is near-identical,
`Run` and the DTO style are parallel). Do not extract a common library on the strength of that
similarity alone — the plan is to factor it out once a third consumer or a real divergence forces
the question. Until then, a change to a shared convention means changing it in both places, and a
new server means copying the conventions again. One scheduled exception: `ToolListing.cs` extracts
into a shared project the next time a protocol revision forces an edit to both copies, in that same
change, because its copies change for the specification rather than for their services. Extract
nothing else with it.

## Commands

```powershell
dotnet tool restore                                 # once per clone: puts nbgv on `dotnet nbgv`
dotnet build
dotnet test
dotnet pack                                         # both servers, as .NET tools, into artifacts/
dotnet nbgv get-version                             # what this checkout would ship as

dotnet run --project src/TeamsMcp -- install        # register in the repository the cwd is inside
dotnet run --project src/TeamsMcp -- auth           # one-time interactive sign-in; primes the token cache
dotnet run --project src/TeamsMcp -- selftest       # silent-auth + Graph round-trip, raw errors to stdout
dotnet run --project src/TeamsMcp -- call           # bare: list the tools; `call <tool> key=value…` invokes one
dotnet run --project src/TeamsMcp                   # MCP server on stdio — needs an MCP client to drive it

dotnet run --project src/AzureDevOpsMcp -- install
dotnet run --project src/AzureDevOpsMcp -- auth
dotnet run --project src/AzureDevOpsMcp -- selftest # silent-auth + connectionData + projects
dotnet run --project src/AzureDevOpsMcp -- config   # validate + print the data files (deployment map)
dotnet run --project src/AzureDevOpsMcp -- call     # bare: list the tools; `call <tool> key=value…` invokes one
dotnet run --project src/AzureDevOpsMcp
```

`dotnet test` covers everything that does not need the remote service: body conversion and
truncation, DTO mapping and skip counting, name resolution, query construction, the auth and
consent logic, the logging stack, the `tools/list` hints and result trimming, the tool annotations,
and all of `install`. Read `tests/` for the current inventory. The tested helpers are `internal`,
reached through `InternalsVisibleTo` in each app csproj — prefer widening to `internal` over
reshaping code for testability.

Anything that talks to Graph or Azure DevOps is still verified by hand: `selftest` exercises the
same silent credential path each server uses, but in console mode where exceptions and output are
visible. Verifying a tool change end-to-end means `-- call <tool> key=value…` — one shot of that
tool through the real server path (same host, silent auth, `Run` wrapper and filters as server
mode, over in-memory pipes), result JSON on stdout, logs on stderr, non-zero exit on a tool error.
Arguments are KEY=VALUE pairs coerced against the tool's own input schema, one JSON object, or `-`
to read that object from stdin; bare `call` lists the tools. Registering the server in an MCP
client (see README) remains the check that the *client* sees what it should.

**Packaging, versioning and CI are in the `mcp-release` skill** (`.claude/skills/mcp-release/`) —
how both servers pack as .NET tools, why the package ids are owner-prefixed, the four nbgv
decisions (including the `publicReleaseRefSpec` spelling trap), and what the two GitHub Actions
workflows must keep true. Read it before touching `version.json`, `Directory.Build.props`, a
csproj's package metadata, or anything under `.github/workflows/`.

Required environment for any mode except a bare build: `TEAMS_MCP_TENANT_ID` /
`TEAMS_MCP_CLIENT_ID` for the Teams server, and `ADO_MCP_TENANT_ID` / `ADO_MCP_CLIENT_ID` /
`ADO_MCP_ORG_URL` for the Azure DevOps one. They are never hardcoded and never committed. The one
hardcoded id in the repo is Azure DevOps' own Entra application id
(`499b84ac-1321-427f-aa17-267ca6975798`) in `AdoContext.ResourceId` — a fixed, first-party, public
identifier for the resource being requested, not a credential.

## Diagnosing a failure

**The log file is the primary diagnostic surface**, because when a server runs under an MCP client
nobody sees stderr — and every error returned to the model carries its `req=N` and the log path, so
an MCP error message leads straight to the lines that explain it. **The `mcp-log-diagnostics`
skill** (`.claude/skills/mcp-log-diagnostics/`) has the log paths, the line format, the `selftest`
recipe, the stable event names and what each one already answers without adding code. Read it
before adding logging to chase a bug — the answer is usually already in the file.

**User-authored text is not logged unless `TEAMS_MCP_LOG_CONTENT` / `ADO_MCP_LOG_CONTENT` is
`true`** — only `{field}.len=N`. Keep it that way when adding tools: use
`TeamsMcpLog.ContentArg` / `AdoMcpLog.ContentArg` for anything carrying Teams conversation content,
work item or pull request descriptions, or comment bodies, and plain `A(...)` / `…Log.Arg` for ids,
counts and flags. Organization names, project names, branch names and area paths are addresses
rather than content and are logged in full — a wrong organization is otherwise invisible. Tenant
and client ids are logged in full too (they are OAuth public identifiers, not secrets); tokens
never are, and the startup banner reports env vars by presence and shape only.

## Architecture constraints

**stdout belongs to the MCP transport.** Each `Program.cs` clears the default providers and
registers two `CompactLoggerProvider`s — a file sink and a **stderr** sink. Never add
`Console.WriteLine`, a stdout sink, or `AddConsole()` (which defaults to stdout) to any code path
that runs in server mode — it corrupts the JSON-RPC stream. The `auth` and `selftest` branches
return before the host is built, so they may write to stdout freely. `call` builds the real host
but moves its transport onto in-memory pipes (`BuildMcpHost`, the one place the transport is
chosen), which is why it may print to stdout and server mode still may not.

**Auth is split in two on purpose.** `-- auth` performs the only interactive flow (device code by
default, `…_AUTH=browser` for the browser flow) and serializes an `AuthenticationRecord` to
`%LOCALAPPDATA%\{teams-mcp|ado-mcp}\auth-record.json` alongside the MSAL persistent token cache
(named `teams-mcp` / `ado-mcp`, DPAPI-protected on Windows). The server path reloads that record
with `DisableAutomaticAuthentication = true`, so it can never prompt over stdio; missing sign-in
throws an `McpException` with instructions instead.

Two settings must stay in sync between the two flows or the cache silently misses:
- The cache `Name` and the `AuthenticationRecord`.
- The CAE flag must be false on both sides — MSAL partitions the persisted cache by CAE flag, and a
  CAE-enabled request would not find the cached refresh token. Teams sets `isCaeEnabled: false` on
  its `AzureIdentityAuthenticationProvider`; Azure DevOps builds every `TokenRequestContext` through
  `AdoContext.RequestContext`, which passes `isCaeEnabled: false`.

Adding a Graph capability usually means adding a scope to `GraphContext.ReadScopes` **and** the app
registration's delegated permissions, then re-running `-- auth` to re-consent. Azure DevOps has no
scope list to extend — it is a single `…/.default` resource scope, so there is nothing there to
reduce and no consent record to keep: a new capability means a new delegated permission on the app
registration (and possibly an organization policy change), then `-- auth` again.

**Mutations are gated.** Teams' two sending tools call `RequireSendEnabled()`
(`TEAMS_MCP_ALLOW_SEND=true`) — and in that server the gate is checked twice over, at the call and
at sign-in, because it also decides whether the send scopes are requested at all (see "Teams' scope
list follows the send gate" in `src/TeamsMcp/CLAUDE.md`). The Azure DevOps server's three write tools — `update_work_item`,
`create_work_item`, `add_pull_request_comment` — call `AdoTools.RequireWriteEnabled()`
(`ADO_MCP_ALLOW_WRITE=true`) before doing anything else, even validating arguments. Any new
mutating tool calls the same helper rather than inventing another policy; `install` never writes
either gate into a repository's config. Work item writes go over JSON Patch
(`AdoClient.PatchAsync` — PATCH updates, POST creates, `application/json-patch+json` both ways,
built in `Writes.cs`), and each write returns the post-write state in the read tools' DTO shapes
so no follow-up read is needed. On a write, an ambiguous name (`assigned_to` through the vssps
identity service, `type` against the project's work item types) fails listing the candidates —
never a guess. Tag *suggestion* stays out of this server deliberately: `add_tags` applies the
caller's explicit list, and deciding what to tag is agent-side tooling's concern.

**`install` edits somebody else's repository, so it is a merge and never a write-over.**
`Install.cs` finds the repository by walking up to a `.git`, decides which MCP client the repository
uses from marker files, and merges one entry into that client's config, preserving other servers and
other top-level properties. Three rules hold it together and should survive any change to it:
- **An entry that already differs is a refusal**, printing both versions, until `--force`. Re-running
  with the same environment is a no-op.
- **Identity is referenced (`${ADO_MCP_TENANT_ID}`), addresses are literal** (organization URL,
  default project), and **mutation gates are never written** — the config usually ends up committed,
  and an app registration and a send/write gate belong to whoever runs the server.
- **Clients are data, not code paths.** A client is a config path, a servers property, an
  env-reference syntax and its marker files (`Install.Clients`); supporting another one means adding
  a row, and anything that cannot be expressed as a row is a reason to reconsider, not to branch.

## Tool conventions

Follow these when adding or editing tools in either server — they are the reason the output is
shaped the way it is.

- **Every tool body is wrapped in `Run(name, args, ...)`.** It assigns the `req=N` correlation id,
  times the call, logs arguments and a result summary, and maps exceptions:
  `AuthenticationRequired` → re-auth instruction, the service's own error type (`ODataError` /
  `AdoApiException`) → readable error, anything else → type + message, all as `McpException` with
  the `req=N` log reference appended. An `McpException` thrown deliberately further down (bad name,
  mutation disabled) passes through untouched and logs at Warning. New tools must go through `Run`,
  passing their arguments via `A(...)` — that is what makes a failed call reconstructible from the
  log.
- **Output is optimized for a model's context window.** The serializer omits nulls (configured in
  `Program.cs`, not by attributes), so DTO fields are nullable and set to `null` when uninteresting:
  Teams emits `messageType` only for non-`Message` messages; Azure DevOps drops a `wellFormed`
  project state, a `succeeded` merge status, a `completed` run status, an area path equal to the
  project, and a description that merely repeats the name. `hasMore`/`skipped`/`truncated` appear
  only when true. Keep this style — do not add fields that are always present-but-empty.
- **Skipped-not-dropped.** Anything filtered out is counted in the `skipped` envelope via
  `SkipCounter`, so a caller can tell "nothing there" from "filtered": deleted and system messages
  in Teams; deleted and system-generated pull request comments, deleted work item comments, and the
  pipeline timeline records that passed in Azure DevOps. Records that never ran are neither listed
  nor counted — they were not filtered, they did not happen.
- **Names resolve to ids leniently.** Teams' `ResolveTeamAsync`/`ResolveChannelAsync` and Azure
  DevOps' single `AdoTools.Resolve` accept an id passthrough (GUID, `19:` prefix, or a number for a
  pipeline), otherwise match display names case-insensitively — exact first, then substring — and
  throw an `McpException` listing the candidates on no-match or ambiguity. New id parameters should
  do the same; in the Azure DevOps server that means calling `Resolve` rather than writing another.
- **Bodies become plain text.** `TeamsTools.HtmlToText` and `Text.FromHtml`/`Text.FromMarkdown` are
  `[GeneratedRegex]` pipelines that deliberately preserve what an agent acts on: links become
  `text (url)`, images become alt text, table cells become `|`, list items become `- `. Markdown
  emphasis is only stripped at word boundaries so `snake_case` identifiers survive. Truncation
  happens after conversion, at `body_limit`, flagged with `truncated: true`. The send direction
  mirrors this: Teams' `format: "markdown"` converts to HTML server-side (`Markdown.ToHtml`),
  with paragraph spacing explicit because Teams renders `<p>` with no margin and collapses
  newlines in a text body — measured facts documented in `docs/teams-server.md` § Sending.
- **Paging is manual and bounded.** Loops follow the service's own continuation (`OdataNextLink`
  with `WithUrl(...)`; `x-ms-continuationtoken`, `$top`/`$skip`, or one-over-the-limit for Azure
  DevOps), stop as soon as `limit` is reached (setting `hasMore`), and break early once results are
  older than `since`. Scans that filter client-side (`list_chats`, `list_pull_requests`) cap how
  much they examine and log a Warning when they hit that cap.
- Tool parameter names use `snake_case` (`include_replies`, `body_limit`, `target_branch`) because
  that is what reaches the model; C# locals and DTO members stay PascalCase/camelCase.
- **Every tool declares whether it changes anything.** A read tool sets `ReadOnly = true`; a
  mutating one sets `Destructive` and `Idempotent` instead, and repeats the gate's environment
  variable in its `[Description]` so the refusal reads as configuration rather than a transient
  failure. The two say different things to different audiences — the annotation is what a client
  gates a confirmation prompt on, the description is what the model reads — so a mutating tool needs
  both. Only the hints that are actually true are set: they are `bool` over `bool?` backing fields,
  so an unset one is omitted rather than sent as `false`, which is the same
  omit-what-is-uninteresting rule the DTOs follow. `OpenWorld` stays unset in both servers — the
  spec's default is already `true`, which is right for a remote organization or tenant. A tool that
  sets neither `ReadOnly` nor a mutation hint fails `ToolListingTests`.
- **The schema is worth having; the second copy of the result is not.** Every tool sets
  `UseStructuredContent = true`, which is what makes the SDK generate an `outputSchema` — a model
  learns the shape of a result before spending a call to find out, which is what makes a chain like
  `get_pull_request` → `add_pull_request_comment` plannable rather than exploratory. The flag also
  makes the SDK send the payload twice, as escaped JSON in a text block *and* as native JSON in
  `structuredContent` — measured as byte-identical, with the text copy the larger of the two because
  it escapes every quote. `ToolResults.Trim` (an `AddCallToolFilter`) drops the text copy, so the
  net cost is the schema alone. Two results keep their text and must continue to: an error, whose
  message is the one thing a caller must be able to read whatever it understands, and a result whose
  structured payload is not a JSON object — a bare array is only legal in `structuredContent` from
  2026-07-28 on, and these servers still answer older clients. Wrapping the remaining bare-array
  tools in an envelope DTO would let them join, and would be the reason to do it.
- **Waiting is a tool, not a held-open request.** `wait_for_pipeline_run`, `wait_for_channel_messages`
  and `wait_for_chat_messages` poll and can run for the better part of an hour, so both servers
  enable the Tasks extension (`.WithTasks`, SEP-2663) with an `InMemoryMcpTaskStore` — correct for
  stdio, where the store dies with the client and there is nothing worth persisting. Only the
  waiters are task-capable: the `ExecutionModeSelector` marks them `Optional` and leaves every other
  tool `Synchronous`, because handing back a handle the caller has to chase is a worse answer than a
  sub-second result. `Optional` rather than `Required` is what makes a client that never negotiated
  the extension still work — it blocks instead, which is why **each waiter bounds its own wait** and
  never relies on the client to give up. Their names are listed in `ToolExecution.LongRunning` and
  checked against the real tool set by a test, since a rename would otherwise silently drop a waiter
  back to blocking. A waiter that runs out of time returns its result with `timedOut: true` rather
  than throwing: "it has not finished" and "it failed" are different answers, and a timeout is the
  first one. `req=N` still correlates throughout, because `Run` sets it inside the tool body and it
  flows to every poll — only `tasks/get` arrives as a separate request with its own id.
- **`tools/list` is a cacheable result.** `ToolListing.Stamp` (wired as an `AddListToolsFilter`, so
  the assembly scan stays the source of the list) sets the `ttlMs`/`cacheScope` that SEP-2549
  requires — a 2026-07-28 client logs a warning when they are missing, and without them must treat
  every listing as immediately stale — and sorts the listing, which the spec asks for so a client
  can cache it and a model's prompt cache stays warm. Both claims depend on the list being fixed at
  compile time: nothing is registered at runtime and the gated tools are always listed, refusing at
  call time rather than being hidden. A server that ever varies its listing per caller has to
  revisit the TTL and `CacheScope.Public` together.
- **Server instructions carry what a tool description cannot.** `ServerInstructions` in each
  `Program.cs` is sized like a system prompt, not documentation: how the server fails, what its
  silences mean (an omitted field is "nothing to say"; `skipped` versus no results), and that a
  gate refusal will not change on retry. It must not restate what is already in a tool's
  `[Description]` — that text is paid for twice.
- **Organization-specific knowledge is configuration, never code.** `deployment_status` is the
  model: the server knows mechanisms — the classic-release chain, the pipeline/Environment chain,
  TFVC path containment and branch walking (`Deployments.cs`) — while which deployables exist and
  what ships each one (release definition + environment, or pipeline + optional ADO Environment
  and branch) live in an external JSON file loaded through `DataFile<T>` (`ADO_MCP_DEPLOYMENTS`,
  default beside the auth record, re-read on timestamp change, unknown fields ignored so other
  consumers can share the file). `note` and its like are opaque passthrough. No TFVC path, release definition or
  heuristic from any one organization belongs in this repo — extend the mechanism, or regenerate
  the data. A new data file means a new `DataFile<T>` + a section in the `-- config` verb.
