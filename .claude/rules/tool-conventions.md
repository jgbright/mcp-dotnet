---
paths:
  - "src/**"
  - "tests/**"
---

# Tool conventions

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
- **`Run` is the tool body, so `ToolErrors.Guard` covers what fails outside it.** Argument binding
  happens above `Run` and throws *past* the call-tool filter, and the SDK's composed handler catches
  it one frame higher and replaces the detail with `An error occurred invoking '<tool>'.` — no
  `req=N`, no log line, nothing to act on. `Guard` (an `AddCallToolFilter`, wired beside
  `ToolResults.Trim`) closes that three ways: it checks the supplied names against the tool's own
  `inputSchema` before dispatch, the same check `Call.Coerce` makes for the command line; it catches
  what escapes the tool; and it gives a `req=N` to any error result that arrives without one, since
  anything already carrying one went through `Run`. `McpException` and `OperationCanceledException`
  are rethrown untouched — which leaves an unknown tool or method name (`McpProtocolException`) as
  the one failure class with no `req=N`, and `ServerInstructions` says so rather than promising
  otherwise. This is one of the deliberately duplicated pieces: both servers carry their own copy.
- **A secret's value is never output.** Where the service marks a value secret, a tool returns the
  name and `isSecret: true` and stops — typed results (`Mapping.ReleaseVariables`), the passthrough
  (`ApiRequest.Mask`, which walks any parsed response for an object carrying `isSecret: true`), and
  configuration search alike, which matches a secret on its name only so a caller cannot narrow the
  value down by asking. Nothing holding secrets is expanded for decoration: a referenced variable
  group is reported as id and name, never its contents, and a deployment agent's capabilities — its
  environment variables, which the service does not mark secret and which carry license keys — are
  never requested at all. Do not add a heuristic that masks anything
  *looking* like a key — it would hide `$(Payments.ApiKey)` in a task input, which is the thing the
  caller needs to see. See `docs/tool-contract.md` § A secret's value is not output.
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
  DevOps), stop as soon as `limit` is reached (setting `hasMore`), and break early once a whole
  page holds nothing newer than `since` — **per page, not per record**. Graph orders Teams messages
  by `lastModifiedDateTime`, so a reaction lifts an old message above genuinely newer ones and one
  out-of-range record is never proof the range is exhausted (`MessagePagingTests`). Scans that
  filter client-side (`list_chats`, `list_pull_requests`) cap how much they examine and log a
  Warning when they hit that cap.
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
- **Waiting is a tool, not a held-open request.** `wait_for_pipeline_run`, `wait_for_pull_request`,
  `wait_for_release`, `wait_for_channel_messages`, `wait_for_chat_messages`, `wait_for_mentions`
  and `wait_for_any_message` poll and can run for the better part of an hour, so both servers
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
