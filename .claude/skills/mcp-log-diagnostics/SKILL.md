---
name: mcp-log-diagnostics
description: Diagnose a failing Teams or Azure DevOps MCP server in this repo using its log file — log paths and line format, the selftest recipe, the stable event names, correlating a req=N from an MCP error back to the lines that explain it, and what the log already answers without adding code. Use when a tool call fails, auth is broken, a name resolves wrongly, a call is being throttled, or you are about to add logging to chase a bug.
---

# Diagnosing a Teams or Azure DevOps MCP server

**The log file is the primary diagnostic surface**, because when a server runs under an MCP client
nobody sees stderr. Default paths (override with `TEAMS_MCP_LOG_DIR` / `ADO_MCP_LOG_DIR`):

```
%LOCALAPPDATA%\teams-mcp\logs\teams-mcp.log
%LOCALAPPDATA%\ado-mcp\logs\ado-mcp.log
```

One line per event, flushed per write, rolling to `.1` at 8 MB, `pid=` on every line since several
server instances can share the file. Format:

```
{utc} {LVL} {pid} {event} req={n} {message}
```

Recipe:

1. `dotnet run --project src/<server> -- selftest` — separates "auth is broken" from "a tool is
   broken", and writes to the same log file. It prints the log path on the first line.
2. `dotnet run --project src/<server> -- call <tool> key=value…` — reproduces one tool call
   through the real server path (host, silent auth, `Run` wrapper, filters) without an MCP
   client: result JSON on stdout, the server's own log lines on stderr, non-zero exit on a tool
   error. Bare `call` lists the tools; arguments can also be one JSON object or `-` for JSON on
   stdin. This is the step that turns "the model saw an error" into a command you can iterate on.
3. Read the top of the log: the `startup` lines report version, runtime, which env vars are set,
   the gate (`sendEnabled` / `writeEnabled`), the log settings, and whether an authentication
   record exists.
4. Grep an event name. The stable ones are `startup`, `auth.config`, `auth.record`,
   `auth.mismatch`, `auth.token`, `auth.fail`, `tool.start`, `tool.ok`, `tool.fail`, `resolve`,
   `page`, `poll`, `config`, `crash`, plus the HTTP pair, which differs by server: `graph.http`/`graph.http.fail` in
   the Teams server, `http`/`http.fail` in the Azure DevOps one.
5. Grep `req=N`. Every tool call gets a correlation id, stamped by the logger from an `AsyncLocal`
   (`TeamsMcpLog.CurrentRequest` / `AdoMcpLog.CurrentRequest`) — so the tool's own events, every
   HTTP call it made, and any MCP SDK event underneath it all carry the same id. **Errors returned
   to the model include their `req=N` and the log path**, so an MCP error message leads straight to
   the lines that explain it.

What the log already answers without adding code:

- *Which call failed and why* — the `*.fail` HTTP line logs method, path, status, duration, the
  support ids (`request-id`/`client-request-id` for Graph, `ActivityId`/`x-vss-e2eid` for Azure
  DevOps), `Retry-After`, and the service's own error body. The body is buffered and put back, so
  the downstream parser still sees it.
- *Whether it is throttling or permissions* — status plus `Retry-After` (and `X-RateLimit-Delay`
  for Azure DevOps) on the same line.
- *Why a name resolved to the wrong thing* — `resolve` (Debug) logs the input, whether it matched
  exactly or by substring, the chosen name/id, and how many candidates there were.
- *Whether sign-in is stale or mismatched* — `auth.record` logs the record's username/tenant/client
  and when it was written; `auth.mismatch` fires at Warning when the env vars no longer match the
  record, which otherwise fails silently because MSAL looks for a token that was never cached.
- *Whether the failure was even reached* — `auth.token` confirms a silent token was acquired and
  when it expires. `GetClientAsync` acquires up front on purpose so auth failures surface as auth
  failures rather than as a confusing error inside an unrelated service call.
- *What a query actually asked for* — `list_work_items` logs the WIQL it generated, and returns it
  in the result.

Levels: `…_LOG_LEVEL=Debug` adds successful HTTP calls, paging and name resolution; `Trace` adds
the MCP SDK's own JSON-RPC traffic (useful for protocol-level problems).

Note the content rule that stays in CLAUDE.md: user-authored text is not logged unless
`TEAMS_MCP_LOG_CONTENT` / `ADO_MCP_LOG_CONTENT` is `true`, so a body you are looking for will show
as `{field}.len=N` unless you set it.
