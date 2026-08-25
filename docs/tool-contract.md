# Tool contract

The conventions every tool in both servers follows. They exist because the caller is a language
model with a finite context window and no way to ask a follow-up question cheaply.

## `Run` wraps every tool body

```csharp
public Task<List<ChannelDto>> ListChannels(string team, CancellationToken ct) =>
    Run("list_channels", A("team", team), async () => { … });
```

`Run` (in `TeamsTools.cs` / `AdoTools.cs`) does five things:

1. Allocates the next `req=N` from a static counter and puts it in the `AsyncLocal` the logger
   reads, so every HTTP call, page, resolution and poll underneath carries the same id.
2. Logs `tool.start` with the tool name and the arguments as passed through `A(…)` / `ContentArg`.
3. Times the call.
4. Logs `tool.ok` with a per-type result summary from `Describe` — counts, ids, `hasMore`, skip
   counts — never the content.
5. Maps exceptions.

The mapping is the part callers see:

| Thrown | Logged at | Model sees |
| --- | --- | --- |
| `McpException` | Warning, `tool.fail … rejected` | Unchanged. It was already a model-facing message (bad name, gate refusal, unparseable timestamp) |
| `AuthenticationRequiredException` | Error | "Sign-in expired or additional consent required. Run `… -- auth` again." + log reference |
| `ODataError` (Teams) | Error | `Graph error {code}: {message}` + log reference |
| `AdoApiException` (Azure DevOps) | Error | `Azure DevOps error {status}: {message}` + log reference |
| `OperationCanceledException` | Warning | Rethrown unchanged — the caller went away |
| anything else | Error, with the full exception | `{TypeName}: {message}` + log reference |

The log reference is literally `(details: grep "req=7" in C:\…\teams-mcp.log)`. That sentence is
the whole diagnostic contract: an error a user pastes back leads straight to the lines that explain
it.

**A new tool must go through `Run`, passing its arguments via `A(…)`.** That is what makes a failed
call reconstructible from the log.

## What fails outside `Run`

`Run` is the tool body, so a call that cannot bind its arguments never enters it. That failure is
raised by the SDK's binder above `Run`, throws *past* the call-tool filter, and is caught one frame
higher by the SDK's composed handler, which replaces it with `An error occurred invoking '<tool>'.`
— no `req=N`, no log line naming the tool, and no hint that the parameter wanted was `release_id`
rather than `releaseId`. The SDK's own literal carries the detail after a colon; what ships has a
period and nothing after it.

`ToolErrors.Guard` (an `AddCallToolFilter`, wired beside `ToolResults.Trim`) closes that, in the
order the three cases fire:

| Case | What `Guard` does |
| --- | --- |
| The supplied names cannot bind to the tool's `inputSchema` | Refuses **before dispatch**, naming the tool, the unknown argument, the missing required one, the full parameter list and what was supplied. An unknown name that is a real parameter in the wrong convention is called out as such — that is the case that produces both faults at once |
| An exception escapes the tool | Caught here, where the detail still exists. Logged in full at `tool.fail`, returned with the same shape |
| An error result arrives carrying no `req=` | Given one. Whatever produced it, it did not go through `Run` |

`McpException` and `OperationCanceledException` are rethrown untouched: the first already carries
`Run`'s own `req=N` and log reference and would only be buried under a second, and the second is not
a failure. `McpProtocolException` derives from `McpException`, so an unknown tool or method name
stays a JSON-RPC error rather than becoming a tool result claiming the call was dispatched — which
makes it the one failure class with no `req=N`, and `ServerInstructions` says so.

`Guard` allocates from the same static counter `Run` does, so a failure caught either side of `Run`
is indistinguishable from one caught inside it when reading the log. The validation is the same
check `Call.Coerce` already makes for the command line, which is where the good error message
already existed. Both servers carry their own copy, per the deliberate-duplication rule.

## Output is optimized for a context window

The serializer is configured once in `Program.cs` with
`DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` — not by attributes on the DTOs. So
the convention is: **make a field nullable and set it to `null` when it is uninteresting**, and it
disappears from the wire.

What "uninteresting" means in practice:

- Teams emits `messageType` only for messages that are not ordinary user messages.
- Azure DevOps drops a `wellFormed` project state, a `succeeded` merge status, a `completed` run
  status, an area path equal to the project, and a description that merely repeats the name.
- `hasMore`, `skipped` and `truncated` appear only when true.

Do not add a field that is always present-but-empty. An empty array or a `false` costs tokens on
every result to say nothing.

The server instructions tell the model how to read that silence: *an omitted field means "nothing
to say", not "unknown".*

## A secret's value is not output

Where the service marks a value secret, tools return the name and the flag and stop. The Azure
DevOps server's release definitions hold `whsec_` and `sk_` values, and a tool that leaks one into a
transcript is worse than no tool: a transcript outlives the call, and the value cannot be un-said.

This is a rule about the *shape* rather than about particular endpoints, which is what makes it hold
where a type does not:

- Typed results null the value and keep `isSecret: true` (`Mapping.ReleaseVariables`).
- `ado_api_request` walks the parsed response and replaces the `value` of any object carrying
  `isSecret: true` with `[redacted]` (`ApiRequest.Mask`) — the same rule applied to a body this
  server has no type for.
- A search over configuration matches a secret on its **name only** (`ReleaseConfig.Matches`).
  Matching on a value that is then withheld would leak it a bit at a time: a caller could ask
  whether it starts with `whsec_` and be told.
- Nothing that holds a secret is expanded for decoration. A referenced variable group is reported as
  id and name; its variables are never read.
- One thing is withheld that the service does *not* mark: a deployment agent's capabilities, which
  are its environment variables and have carried a license key on a real agent. No Azure DevOps tool
  requests that expansion ([azure-devops-server.md](azure-devops-server.md#where-a-stage-lands)).
  That is a decision about a whole bag, not a heuristic about values, which is why it does not
  contradict the next paragraph.

Heuristics on the value — masking anything that *looks* like a key — are deliberately not part of
this. They would hide `$(Stripe.ApiKey)` in a task input, which is exactly what the caller needs to
see, while still missing whatever the heuristic did not anticipate.

## Skipped, not dropped

Anything filtered out is counted in a `skipped` envelope via `SkipCounter`, so a caller can tell
"nothing there" from "filtered":

| Server | Counted | Why filtered |
| --- | --- | --- |
| Teams | `deleted`, `system` | Tombstones and member-added/renamed events are noise unless asked for |
| Azure DevOps | `deleted`, `system` | Deleted work item comments; deleted and system-generated PR comments (pushes, votes, policy results) |
| Azure DevOps | `succeeded` | Pipeline timeline records `get_pipeline_run` does not report because they passed |

Records that never ran are neither listed nor counted — they were not filtered, they did not happen.

Counting happens during mapping, before any cap is applied, so the counts cover everything examined
rather than only what fit.

## Names resolve to ids leniently

A caller usually has a name and not an id. Both servers accept either.

The rule, implemented once per server — `AdoTools.Resolve`, and
`ResolveTeamAsync`/`ResolveChannelAsync` in Teams:

1. An input that already looks like an id passes straight through: a GUID, a `19:` prefix, or a
   number for a pipeline, release definition or environment.
2. Otherwise match display names case-insensitively — **exact first, then substring**.
3. Exactly one match wins, and the resolution is logged at Debug (`resolve`) with which rule matched
   and how many candidates there were.
4. Anything else throws an `McpException` **listing the candidates**. No match lists what was
   available; ambiguity lists what matched and says to use the id.

Never guess. On a write this matters more than anywhere else: `assigned_to` resolves through the
vssps identity service and `type` against the project's own work item types, and an ambiguous name
is an error rather than a coin flip.

New id parameters should do the same. In the Azure DevOps server that means calling `Resolve` rather
than writing another one.

## Bodies become plain text

`TeamsTools.HtmlToText` and `Text.FromHtml` / `Text.FromMarkdown` are `[GeneratedRegex]` pipelines
that deliberately preserve what an agent acts on and drop the rest:

| Input | Output |
| --- | --- |
| `<a href="u">label</a>` | `label (u)` |
| `<img alt="x">` | `x` |
| `</td>`, `</th>` | ` \| ` |
| `<li>` | `\n- ` |
| everything else | stripped, entities decoded, `&nbsp;` normalized, runs of blank lines collapsed |

Markdown gets the same treatment plus autolinks, fences, headings, rules, quotes and bullets.
**Emphasis markers are only stripped at word boundaries**, so `snake_case` identifiers and
`System.Title_2` survive — that is what the lookarounds in `StrongUnderscoreRegex` and
`ItalicUnderscoreRegex` are for.

Truncation happens **after** conversion, at `body_limit`, flagged with `truncated: true`, and cuts
back one character when the limit would split a surrogate pair — emoji are routine and half of one
is an invalid character.

## Paging is manual and bounded

No tool returns "everything". Every loop follows the service's own continuation, stops as soon as
`limit` is reached (setting `hasMore`), and breaks early once results are older than `since`.

| Continuation | Used by |
| --- | --- |
| `OdataNextLink` + `WithUrl(…)` | Graph collections |
| `x-ms-continuationtoken` header | Azure DevOps projects, pipelines |
| `$top` / `$skip` | Azure DevOps pull requests |
| One over the limit | Azure DevOps builds, WIQL ids — answers `hasMore` without a second request |

**Scans that filter client-side cap how much they examine and log a Warning when they hit the cap.**
`list_chats` stops after 500 chats, `list_pull_requests` after 500 pull requests, Teams search after
8 pages of 25, `deployment_status` after 500 release definitions / 1000 build definitions / 10 TFVC
paths. Hitting a cap sets `hasMore` — it is never passed off as a complete answer.

## Every tool declares whether it changes anything

A read tool sets `ReadOnly = true`. A mutating one sets `Destructive` and `Idempotent` instead, and
**repeats the gate's environment variable in its `[Description]`**.

The two say different things to different audiences, which is why a mutating tool needs both: the
annotation is what a client gates a confirmation prompt on, and the description is what the model
reads — so a refusal reads as configuration rather than as a transient failure worth retrying.

Only the hints that are actually true are set. They are `bool` over `bool?` backing fields, so an
unset one is omitted rather than sent as `false` — the same omit-what-is-uninteresting rule the DTOs
follow. `OpenWorld` stays unset in both servers: the spec's default is already `true`, which is right
for a remote organization or tenant.

| Tool | Hints |
| --- | --- |
| every read tool, waiters included | `ReadOnly = true` |
| `send_channel_message`, `send_chat_message` | `Destructive = false, Idempotent = false` — a send adds and edits nothing, but sending twice posts twice |
| `create_work_item`, `add_pull_request_comment`, `run_pipeline` | `Destructive = false, Idempotent = false` — same reasoning |
| `update_work_item` | `Destructive = true, Idempotent = false` — it overwrites fields that already had values |
| `deploy_release`, `approve_release` | `Destructive = true, Idempotent = false` — deploying replaces what is running in that environment, and approving a pre-deploy gate is what lets it happen. This is the annotation a client hangs its confirmation prompt on, and these are the calls that most deserve one |

A tool that sets neither `ReadOnly` nor a mutation hint fails `ToolListingTests`.

## The schema is worth having; the second copy of the result is not

Every tool sets `UseStructuredContent = true`, which is what makes the SDK generate an
`outputSchema`. A model learns the shape of a result before spending a call to find out, which is
what makes a chain like `get_pull_request` → `add_pull_request_comment` plannable rather than
exploratory.

The flag also makes the SDK send the payload **twice**: as escaped JSON in a text block *and* as
native JSON in `structuredContent`. Measured, they are byte-identical, with the text copy the larger
of the two because it escapes every quote. `ToolResults.Trim`, wired as an `AddCallToolFilter`,
drops the text copy — so the net cost is the schema alone.

Two results keep their text and must continue to:

- **An error**, whose message is the one thing a caller must be able to read whatever else it
  understands.
- **A result whose structured payload is not a JSON object.** A bare array is only legal in
  `structuredContent` from 2026-07-28 on, and these servers still answer older clients. Wrapping the
  remaining bare-array tools in an envelope DTO would let them join, and would be the reason to do
  it.

## `tools/list` is a cacheable result

`ToolListing.Stamp`, wired as an `AddListToolsFilter` so the assembly scan stays the source of the
list, sets the `ttlMs` / `cacheScope` that SEP-2549 requires. A 2026-07-28 client logs a warning
when they are missing and must otherwise treat every listing as immediately stale. The TTL is one
hour: long enough that a client re-lists on restart rather than on a timer, short enough that a
version upgrade underneath a long-lived client is picked up the same day.

`Stamp` also sorts the listing, which the spec asks for so a client can cache it and a model's
prompt cache stays warm — `WithToolsFromAssembly` lists in reflection order, which is not stable
across builds. Sorting is skipped on a paginated response, where reordering one page of many would
misreport the cursor the underlying handler issued against its own order.

Both claims depend on the list being fixed at compile time: nothing is registered at runtime, and
**the gated tools are always listed**, refusing at call time rather than being hidden. A server that
ever varies its listing per caller has to revisit the TTL and `CacheScope.Public` together.

## Waiting is a tool, not a held-open request

`wait_for_pipeline_run`, `wait_for_pull_request`, `wait_for_release`, `wait_for_channel_messages`,
`wait_for_chat_messages`, `wait_for_mentions` and `wait_for_any_message` poll, and can run for the
better part of an hour. Both servers enable the
Tasks extension (`.WithTasks`, SEP-2663) with an `InMemoryMcpTaskStore` — correct for stdio, where
the store dies with the client and there is nothing worth persisting.

Only the waiters are task-capable. The `ExecutionModeSelector` marks them `Optional` and leaves
every other tool `Synchronous`, because handing back a handle the caller has to chase is a worse
answer than a sub-second result.

`Optional` rather than `Required` is what makes a client that never negotiated the extension still
work — it blocks instead. Which is why **each waiter bounds its own wait** and never relies on the
client to give up: timeouts and poll intervals are clamped in code, not taken on trust.

Their names are listed in `ToolExecution.LongRunning` and checked against the real tool set by a
test, since a rename would otherwise silently drop a waiter back to blocking.

**A waiter that runs out of time returns its result with `timedOut: true` rather than throwing.**
"It has not finished" and "it failed" are different answers, and a timeout is the first one. `req=N`
correlates throughout, because `Run` sets it inside the tool body and it flows to every poll — only
`tasks/get` arrives as a separate request with its own id.

## Parameter naming

Tool parameter names are `snake_case` (`include_replies`, `body_limit`, `target_branch`) because
that is what reaches the model. C# locals and DTO members stay PascalCase/camelCase. The DTO
casing is handled by the serializer; the parameter casing is not, so it is written that way in the
signature.

## Server instructions carry what a tool description cannot

`ServerInstructions` in each `Program.cs` is sized like a system prompt, not documentation. It
carries how the server fails, what its silences mean, and that a gate refusal will not change on
retry. It must not restate what is already in a tool's `[Description]` — that text is paid for
twice, once in the instructions and once in the listing.

## Checklist for a new tool

- [ ] On the existing `[McpServerToolType]` class, with `[McpServerTool(Name = "snake_case", …)]`
- [ ] `UseStructuredContent = true`
- [ ] `ReadOnly = true`, or `Destructive`/`Idempotent` plus the gate variable named in the description
- [ ] Body wrapped in `Run(name, A(…) + …, async () => …)`
- [ ] Content-bearing arguments logged with `ContentArg`, everything else with `A`
- [ ] Mutating? `RequireSendEnabled()` / `RequireWriteEnabled()` first, before argument validation
- [ ] Ids accepted wherever a name is, through the shared resolver
- [ ] `limit` clamped; a client-side filter capped and the cap logged as a Warning
- [ ] Bodies converted to plain text, truncated at `body_limit`, flagged `truncated`
- [ ] Filtered records counted into `skipped`
- [ ] Anything the service marks secret returned as name + flag, never as a value
- [ ] DTO fields nullable and nulled when uninteresting
- [ ] `Describe` extended so `tool.ok` summarizes the new result type
- [ ] Long-running? Added to `ToolExecution.LongRunning`, bounds its own wait, returns `timedOut`
