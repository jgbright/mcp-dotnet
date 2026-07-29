# Observability

The log file is the primary diagnostic surface. When a server runs under an MCP client nobody sees
stderr, so everything is also written to a file — and every error returned to the model carries its
`req=N` and the log path, so an MCP error message leads straight to the lines that explain it.

The [`mcp-log-diagnostics`](../.claude/skills/mcp-log-diagnostics/SKILL.md) skill is the operational
side of this: paths, recipes, and what the log already answers without adding code. This document is
the design.

## The stack

Four pieces, duplicated near-identically between the servers in `Logging.cs`:

| Piece | Role |
| --- | --- |
| `ILineSink` | Somewhere to put a formatted line |
| `FileLineSink` | Appends to one file, rolls to `.1` past 8 MB, flushes every line, **never throws** |
| `StderrLineSink` | The other sink. stdout belongs to the transport and must never be written to |
| `CompactLoggerProvider` / `CompactLogger` | Formats one line per event and stamps the correlation id |

`Program.cs` clears the default providers and registers two `CompactLoggerProvider`s, one per sink.
`…McpLog.CreateFactory()` builds the same pair for the console verbs, so `auth` and `selftest` write
to the same file the server does — which is what makes `selftest` a useful diagnostic rather than a
separate universe.

`FileLineSink` flushes each line so a crash still leaves the last event on disk, and opens with
`FileShare.ReadWrite` so several server processes can append to the same file (they do — every MCP
client that registers the server launches its own). A failed write complains to stderr, drops the
stream and returns; a broken log must not break the server. The roll tolerates another process
holding the file by continuing to append to the current one.

## The line format

```
{utc} {LVL} {pid} {event} req={N} {message}
```

```
2026-07-28T07:03:12.441Z INF 30724 tool.start req=7 read_channel_messages team="Platform" channel="General" limit=20
2026-07-28T07:03:12.902Z DBG 30724 graph.http req=7 GET /v1.0/teams/{id}/channels/{id}/messages -> 200 ms=431 request-id="298a99a3-…"
2026-07-28T07:03:12.905Z INF 30724 tool.ok req=7 read_channel_messages ok ms=464 messages=20 hasMore=true skipped.system=3
```

- The **pid** matters because several instances share the file.
- The **event name** comes from the `EventId`, falling back to the shortened category. It is the
  grep anchor.
- **`req=N`** is stamped by `CompactLogger` from `…McpLog.CurrentRequest`, an `AsyncLocal` that
  `Run` sets — not passed down as a parameter. That is why HTTP handler lines and SDK events inside
  a tool call carry it too.
- The **message** is passed as a log *argument* rather than as the template
  (`log.Log(level, ev, ex, "{Msg}", message)`), so braces in a REST error body or a WIQL query can
  never break formatting.
- An **exception** is appended underneath, indented, with type, message, stack and up to four levels
  of inner exception, so the primary line stays greppable.

`Arg(name, value)` formats one ` name=value` pair and returns `""` for null, so an absent argument is
omitted rather than written as `null`. Strings are quoted, truncated at 300 characters, and have
quotes and newlines neutralized. Backslashes are deliberately **not** escaped: nearly every quoted
value is a Windows path or an area path, and `C:\\Users\\…` is worse to read and to paste than the
ambiguity is worth.

## What is and is not logged verbatim

**User-authored text is not logged unless `TEAMS_MCP_LOG_CONTENT` / `ADO_MCP_LOG_CONTENT` is
`true`** — only `{field}.len=N`, which still distinguishes empty from non-empty.

| Use | For |
| --- | --- |
| `ContentArg` | Teams conversation content, work item and pull request descriptions, comment bodies, search queries |
| `A(…)` / `Arg` | Ids, counts, flags, timings |
| `Arg`, in full | Organization names, project names, branch names, area paths, file paths |

Addresses are logged in full on purpose: a wrong organization is otherwise invisible. Tenant and
client ids are logged in full too, because they are OAuth public identifiers rather than secrets.
**Tokens never are**, and the startup banner reports environment variables by presence and shape
only — `Diagnostics.Describe` answers `<unset>`, `<guid 1a2b3c4d…>` or `<set len=44>`.

## The event vocabulary

Stable names, identical in both servers except for the HTTP pair.

| Event | Level | Says |
| --- | --- | --- |
| `startup` | Information | Three lines: build/runtime/pid/cwd, then the config, then the auth state |
| `shutdown` | Information | The host stopped cleanly |
| `crash` | Critical/Error | Unhandled exception, unobserved task exception, or host termination |
| `auth.config` | Debug | Which tenant/client/cache/record a client is being built against |
| `auth.record` | Information | The record loaded, with username, tenant, client, authority and its write time |
| `auth.mismatch` | Warning | The record disagrees with the environment, or (Teams) consent falls short of the requested scopes |
| `auth.token` | Information | A token was acquired silently, with expiry and duration |
| `auth.fail` | Error | No record, or silent acquisition failed |
| `auth.interactive` | Information | The `-- auth` flow: starting, device code issued, signed in |
| `http` / `graph.http` | Debug | A successful request: method, path, status, ms, service request id |
| `http.fail` / `graph.http.fail` | Warning | A failed one, **with the response body** — the service puts the real reason there — plus `Retry-After` |
| `tool.start` | Information | Tool name and arguments |
| `tool.ok` | Information | Duration and a result summary from `Describe` |
| `tool.fail` | Warning/Error | `rejected` (an `McpException`), `auth-required`, `graph-error`/`ado-error`, `cancelled`, or `unhandled` |
| `resolve` | Debug | A name resolved to an id: which rule matched, and out of how many candidates |
| `page` | Debug/Warning | A continuation followed; **Warning** when a scan cap was hit and results may be incomplete |
| `poll` | Debug/Information | A waiter polled, found something, or gave up |
| `config` | Information | A data file was loaded, with path, entry count and write time |

The HTTP line carries the identifier the vendor's support asks for: Graph's `request-id` and
`client-request-id`, Azure DevOps' `ActivityId` and `x-vss-e2eid`. Throttling shows as
`retry-after` and, for Azure DevOps, `X-RateLimit-Delay`.

The logging handler is registered **innermost** in both servers, so it sees each retry attempt
individually rather than only the outcome the retry handler settled on. It also buffers a failed
response body — replacing the content with an equivalent `ByteArrayContent` — so the body can be
logged and still be read by the SDK's own error parser afterwards.

## Levels

| `…_LOG_LEVEL` | Adds |
| --- | --- |
| `Information` (default) | Startup, auth, tool start/ok/fail, waiters giving up, config loads |
| `Debug` | Successful HTTP calls, paging, name resolution, per-poll lines |
| `Trace` | The MCP SDK's own JSON-RPC traffic — how the `initialize` handshake and every request actually looked |

`Trace` is how the protocol-level questions get answered; it is what established which capabilities
Claude Code advertises.

## Triage order

1. **`-- selftest`.** It separates an auth problem from a tool problem and prints raw errors to the
   console. For Azure DevOps, `-- config` does the same for the data files.
2. **The `startup` lines at the top of the log.** Which build, which tenant/client shape, which
   organization, whether sign-in has happened, whether the gates are on, where the data files are
   and whether they exist.
3. **The failing `req=N`.** `grep "req=7"` gives the tool call, every HTTP request it made, and the
   exception — in order.
4. **The `http.fail` line's `body=…`.** The service's own error message is almost always more
   specific than the mapped one.
