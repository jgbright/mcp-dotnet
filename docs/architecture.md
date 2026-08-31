# Architecture

Two .NET 10 console applications, each an MCP **stdio** server built on the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK, pinned at
**2.0.0** (protocol revision 2026-07-28, negotiating down to 2025-11-25 and 2024-11-05).

| Project | Command | Talks to | Client |
| --- | --- | --- | --- |
| `src/TeamsMcp` | `teams-mcp` | Microsoft Graph | `GraphServiceClient` (Kiota-generated SDK) |
| `src/AzureDevOpsMcp` | `ado-mcp` | Azure DevOps REST | `AdoClient`, a hand-rolled typed `HttpClient` wrapper |

Each has a test project under `tests/`; all four are in `McpServers.slnx`.

## The servers share no code, on purpose

`Logging.cs` is near-identical between them. `Run`, `ToolListing`, `ToolResults`, the DTO style and
`Install.cs` are parallel.

Do not extract a common library on the strength of the similarity alone. Factor one out when a
third consumer or a real divergence forces the question. Until then a change to a shared convention
means changing it in both places, and a new server means copying the conventions again.

`ToolListing.cs` is the one scheduled exception: its two copies change for a protocol revision
rather than for their services, so it extracts into a shared project the next time a revision forces
an edit to both copies, in that same change.

The two are independent processes with independent failure modes and independent release-blocking
risk. A shared library would couple their release cadences and need a seam for every place Graph and
Azure DevOps differ, which is most places below the top layer.

## Layers within one server

```mermaid
flowchart TD
    A["Program.cs — verbs, host, DI, filters, ServerInstructions"] --> B["Tools — TeamsTools / AdoTools"]
    B --> C["Run wrapper — req=N, timing, exception mapping"]
    C --> D["Context — GraphContext / AdoContext: silent auth + cached client"]
    D --> E["Service client — GraphServiceClient / AdoClient"]
    E --> F["Logging DelegatingHandler — one line per HTTP call"]
    B --> G["Pure helpers — Search, Text, Writes, Deployments, Mapping"]
    A --> H["Logging.cs — sinks, CompactLogger, Ev, Diagnostics"]
```

- `Program.cs` is composition only: verb dispatch, logger providers, DI registration, serializer
  options, server instructions, the Tasks extension, the two request filters. No service logic.
- One `[McpServerToolType]` class per server is the MCP surface, discovered by
  `WithToolsFromAssembly`. Nothing is registered at runtime, which lets `tools/list` be advertised
  as cacheable (see [tool-contract.md](tool-contract.md)).
- The context owns authentication and the client: a singleton that builds its client lazily behind a
  `SemaphoreSlim`, acquires a token up front so an auth failure is reported as one, and never
  prompts.
- Pure helpers hold everything testable: query construction, body conversion, mapping, patch
  documents, cursor encoding. They are `internal`, reached from the tests through
  `InternalsVisibleTo`. Prefer widening to `internal` over reshaping code for testability.

## The life of a tool call

1. The client sends `tools/call`. `AddCallToolFilter` wraps the invocation; `ToolErrors.Guard` runs
   first inside it, checking the supplied argument names against the tool's own `inputSchema` and
   refusing before anything runs if they cannot bind.
2. The SDK deserializes arguments onto the method's parameters.
3. `Run(name, argsLog, …)` allocates the next `req=N`, stashes it in an `AsyncLocal`, logs
   `tool.start` with the arguments, and starts a stopwatch.
4. `context.GetClientAsync(ct)`. First call per process: read the authentication record, acquire a
   token silently, build the client. Later calls return the cached instance.
5. Names resolve to ids, the service is called (possibly in a paging or poll loop), wire types map
   to DTOs.
6. `Run` logs `tool.ok` with a result summary, or maps the exception to an `McpException` and logs
   `tool.fail`.
7. The SDK serializes the DTO with `DefaultIgnoreCondition = WhenWritingNull`, filling both
   `content` and `structuredContent`; `ToolResults.Trim` drops the duplicate text copy. `Guard` also
   gives a `req=N` to any error result lacking one, since anything carrying one went through `Run`.

Every HTTP request the call made carries the same `req=N`: the logger stamps it from the
`AsyncLocal` rather than it being passed down, so a failure is reconstructible from the log alone.

## Process modes

Both `Program.cs` files parse verbs through System.CommandLine. Each console verb is a subcommand;
the server is the root command's action, reached only when no verb is given. A mistyped verb is a
parse error on stderr with the valid commands suggested, not a silently started stdio server, and
`--help` enumerates the verbs. Verb actions may write to stdout freely; server mode may not. Most
verbs never build the host at all. `install` parses twice on purpose: it owns its option parsing
(`Install.Options`, which the tests drive), the System.CommandLine declarations mirror that surface
only for help and error reporting, and the action hands the tokens back to `Install.Run`.

`call` is the exception to the stdout rule. It builds the same host as server mode, through the
shared `BuildMcpHost` local function, but over in-memory pipes instead of stdio, driven by an
in-process MCP client. One tool call per run: bare `call` lists the tools; `call <tool>` takes
KEY=VALUE pairs coerced against the tool's own `inputSchema`, one JSON object, or `-` for a JSON
object on stdin. `Call.cs` holds that parsing and the result rendering, and the tool name completes
via a System.CommandLine completion source. The name given on the command line resolves the way the
servers' own name parameters do: exact first, then substring, candidates listed on ambiguity, with
the correction noted on stderr. Since the transport is not stdout, the result JSON is the only thing
written there, logs stay on stderr and in the file, and a tool error exits non-zero, so output pipes
cleanly into `ConvertFrom-Json` or `jq`. A call that succeeds has exercised the same path an MCP
client would: host, silent auth, `Run` wrapper, serializer and filters.

| Mode | Teams | Azure DevOps | Writes stdout | Builds the host |
| --- | --- | --- | --- | --- |
| `install` | yes | yes | yes | no |
| `auth` | yes | yes | yes | no |
| `selftest` | yes | yes | yes | no |
| `config` | — | yes | yes | no |
| `call` | yes | yes | yes — the result | yes, over pipes |
| *(no argument)* — MCP server on stdio | yes | yes | **never** | yes |

**stdout belongs to the MCP transport.** In server mode the default logging providers are cleared
and two `CompactLoggerProvider`s are registered, a file sink and a **stderr** sink. A
`Console.WriteLine`, a stdout sink or an `AddConsole()` on any path reachable from server mode
corrupts the JSON-RPC stream.

## File map

### `src/TeamsMcp`

| File | Holds |
| --- | --- |
| `Program.cs` | Verbs, host, DI, `ServerInstructions`, Tasks, the two request filters, crash handlers |
| `GraphContext.cs` | Scope lists and the send gate, interactive and silent auth, `ScopeConsent`, `GraphLoggingHandler` |
| `TeamsTools.cs` | Every tool, the message pager, the wait loop, watermarks and cursors, `Run`, HTML→text, the DTOs |
| `Search.cs` | KQL construction and the untyped mapping of a Microsoft Search hit |
| `ToolListing.cs` | `ToolExecution.LongRunning`, `ToolResults.Trim`, `ToolListing.Prepare` |
| `Logging.cs` | `TeamsMcpLog`, `Diagnostics`, `Ev`, the sinks and `CompactLogger` |
| `Install.cs` | The `install` verb: repository discovery, client detection, config merge |
| `Call.cs` | The `call` verb's pure half: schema-driven argument coercion, result rendering, tool names for completion |

### `src/AzureDevOpsMcp`

| File | Holds |
| --- | --- |
| `Program.cs` | Verbs (including `config`), host, DI, `ServerInstructions`, Tasks, filters, crash handlers |
| `AdoContext.cs` | Resource id and scopes, auth, `BearerTokenHandler`, `AdoLoggingHandler`, `AdoApiException`, `AdoClient` |
| `AdoTools.cs` | Every tool, `deployment_status`' chains, `Run`, WIQL construction, `Resolve` and the resolvers, list/paging helpers |
| `AdoModels.cs` | Wire types and `Mapping` — the wire→DTO layer and the output DTOs |
| `Writes.cs` | JSON Patch documents, the parent relation, tag merging, identity plumbing |
| `Deployments.cs` | The deployment map's shape and validation, the vsrm host, TFVC mapping parsing, path containment |
| `DataFiles.cs` | `DataFile<T>`: the one mechanism for externally configured data |
| `Search.cs` | The almsearch host and the shared search request body |
| `ReleaseConfig.cs` | A release definition flattened to the settings it holds, and the pattern match over them |
| `ApiRequest.cs` | The passthrough's pure half: host resolution, the in-organization url guard, secret masking, the response projection |
| `AuthStatus.cs` | Reading the persisted sign-in for reporting, and the AZURE_DEVOPS_PAT probe |
| `Text.cs` | HTML and Markdown to plain text, truncation, the sentence inside an HTML error page |
| `ToolListing.cs`, `Logging.cs`, `Install.cs`, `Call.cs` | As above |

## State and concurrency

Everything a server holds is per process and dies with it. No database, no shared cache between the
two servers, nothing persisted except the auth material and the log.

| State | Where | Guarded by |
| --- | --- | --- |
| The service client | `GraphContext._client` / `AdoContext._client`, singleton | `SemaphoreSlim` with a double-check |
| The bearer token (Azure DevOps only) | `BearerTokenHandler._token` | `volatile` immutable record for the lock-free read, `SemaphoreSlim` for the refresh |
| The `req=N` counter | `static int _sequence` in each tool class | `Interlocked.Increment` |
| The in-flight `req=N` | `AsyncLocal<string?>` in `…McpLog` | Ambient per async flow; `Run` saves and restores the previous value |
| Data files | `DataFile<T>._cache` | `Lock`; re-parsed when the file's timestamp changes |
| Task handles for the waiters | `InMemoryMcpTaskStore` | The SDK; correct for stdio, where the store dies with the client |
| The log file handle | `FileLineSink._stream` | `Lock`; opened `FileShare.ReadWrite` so several processes can append |

Tool calls can be concurrent and nothing in either server assumes otherwise: the tools are stateless
methods over an immutable client, and the shared mutable state is the table above.

## Cross-cutting invariants

- **stdout belongs to the MCP transport** (above).
- **Mutations are opt-in per environment**, through `TEAMS_MCP_ALLOW_SEND` and
  `ADO_MCP_ALLOW_WRITE`. A new mutating tool calls the existing gate helper
  (`TeamsTools.RequireSendEnabled` / `AdoTools.RequireWriteEnabled`) before anything else, including
  argument validation, and repeats the variable's name in its own `[Description]`. `install` never
  writes a gate into a repository's config. There is one second gate, `ADO_MCP_ALLOW_APPROVE` for
  `approve_release`, required *in addition to* the write gate rather than instead of it, because
  answering a release approval records the signed-in person as having authorized a production
  deployment. That is a different permission, not a bigger one; "this call feels risky" is not a
  reason to add a third.
- **User-authored content is not logged by default.** `ContentArg` for message bodies, descriptions
  and comments; plain `Arg` for ids, counts, flags and addresses. See
  [observability.md](observability.md).
- **Organization-specific knowledge is configuration, never code.** No tag name, TFVC path, release
  definition or per-organization heuristic belongs in this repository. Extend the mechanism and put
  the facts in an external data file, loaded through `DataFile<T>`.
- **Output is shaped for a model's context window.** Nulls omitted, uninteresting fields nulled,
  filtered items counted rather than dropped, `snake_case` parameter names. See
  [tool-contract.md](tool-contract.md).

## Testing boundary

`dotnet test` covers everything that does not need the remote service: body conversion and
truncation, DTO mapping and skip counting, name resolution, WIQL and KQL construction, cursor
encoding, the auth and consent logic, the logging stack, the `tools/list` hints and result trimming,
the tool annotations, and all of `install`. Read `tests/` for the current inventory.

These test files enforce conventions rather than behaviour:

- `ToolListingTests` — every tool declares either `ReadOnly` or a mutation hint, every name in
  `ToolExecution.LongRunning` is a tool that exists, and the listing is stamped and sorted.
- `ToolOutputShapeTests` — the null-omitting serializer contract.
- `ToolRunTests` — the `Run` wrapper's exception mapping and log lines.

Anything that talks to Graph or Azure DevOps is verified by hand. `-- selftest` exercises the same
silent credential path each server uses, in console mode where exceptions and output are visible.
Verifying a tool change end to end means `-- call <tool> key=value…`, which drives that one tool
through the real server path with no MCP client on the other end. Registering the server in an MCP
client remains the check that the client sees what it should. `scripts/rebuild.ps1` is the inner
loop.

## Extending

**Adding a tool.** Put it on the existing tool class with `[McpServerTool]` + `[Description]`; set
`UseStructuredContent = true` and either `ReadOnly = true` or the mutation hints; wrap the body in
`Run(name, A(…) + …, async () => …)`; return a DTO whose uninteresting fields are nullable and
nulled. If it mutates, call the gate helper first. The checklist is in
[tool-contract.md](tool-contract.md#checklist-for-a-new-tool).

**Adding a Graph capability.** Add the scope to `GraphContext.ReadScopes` *and* the app
registration's delegated permissions, then re-run `-- auth` to re-consent. See
[authentication.md](authentication.md).

**Adding an Azure DevOps capability.** There is no scope list to extend; it is a single
`…/.default` resource scope. A new capability means a new delegated permission on the app
registration, possibly an organization policy change, then `-- auth` again.

**Adding externally configured data.** A new `DataFile<T>` with its environment variable, default
file name and format hint, plus a section in the `-- config` verb so the data can be validated
without driving the tools through an MCP client.

**Adding a server.** Copy the conventions rather than extracting them (above), give it its own
`…_TENANT_ID` / `…_CLIENT_ID` pair, its own cache name and log file, and add it to `McpServers.slnx`,
the plugin's `.mcp.json`, and the release workflow. The `mcp-release` skill covers the packaging
side.
