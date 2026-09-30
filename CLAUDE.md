# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Two .NET 10 console apps, each running as an **MCP stdio server**, both using the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK.

The SDK is pinned at **2.0.0**, which speaks the **2026-07-28** protocol revision and negotiates
down to 2025-11-25 (and 2024-11-05) for older clients — verified by hand: an old-style `initialize`
still completes against both servers. Most of that revision is about making HTTP stateless and so
never reaches a stdio server, but four things do, all covered in the tool conventions below:
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
dotnet build -warnaserror                           # CI builds this way; a warning here is a red build there
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

**Mutations are gated, and one of them twice.** Teams' sending and reaction tools call `RequireSendEnabled()`
(`TEAMS_MCP_ALLOW_SEND=true`) — and in that server the gate is checked twice over, at the call and
at sign-in, because it also decides whether the send scopes are requested at all (see "Teams' scope
list follows the send gate" in `src/TeamsMcp/CLAUDE.md`; the reaction tools ride those same send
scopes, so they widen nothing). The Azure DevOps server's six write tools — `update_work_item`,
`create_work_item`, `add_pull_request_comment`, `run_pipeline`, `deploy_release`,
`approve_release` — call
`AdoTools.RequireWriteEnabled()` (`ADO_MCP_ALLOW_WRITE=true`) before doing anything else, even
validating arguments. Any new
mutating tool calls the same helper rather than inventing another policy; `install` never writes
any gate into a repository's config. `ado_api_request` calls that same helper for any method other
than GET or HEAD while staying annotated `ReadOnly`, because it reads under every configuration
this server ships with — the one tool where the annotation and the gate answer different questions,
and the reason its `[Description]` names the variable although it is not a write tool.

`approve_release` is the one exception, and a deliberate one: it calls `RequireApprovalEnabled()`
(`ADO_MCP_ALLOW_APPROVE=true`) **as well as**, never instead of, the write gate. Writing says an
agent may change what other people see; answering a release approval acts as the signed-in human
in a control that exists to require one, and the audit trail names that person as having
authorized the deployment. The two are different permissions, so one variable cannot honestly
carry both. This is not a precedent for per-tool gates — the argument is the audit trail, not that
the call feels risky. See `src/AzureDevOpsMcp/CLAUDE.md`. Work item writes go over JSON Patch
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

How tools are shaped — `Run`, output style, paging, annotations, structured content, waiters,
`tools/list` caching, server instructions — lives in `.claude/rules/tool-conventions.md`, which loads
when a file under `src/` or `tests/` is read. Read it first when planning a tool change before opening
any code.
