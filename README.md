# mcp-dotnet

Two MCP stdio servers on .NET 10, built on the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK, signing in to
Entra ID through Azure.Identity:

- **Teams** (`teams-mcp`): Microsoft Teams via Microsoft Graph. Read teams, channels, chats and
  messages, search, wait for new messages, and send behind an opt-in gate.
- **Azure DevOps** (`ado-mcp`): projects, repositories, pull requests, work items, pipelines and
  search (code, work items, wiki) via the REST API, plus gated work item writes and pull request
  comments.

```
src/TeamsMcp/                 the Teams server
src/AzureDevOpsMcp/           the Azure DevOps server
tests/TeamsMcp.Tests/         xUnit tests for the pure logic, run with `dotnet test`
tests/AzureDevOpsMcp.Tests/
scripts/                      rebuild.ps1 (dev inner loop)
McpServers.slnx               all four projects
```

The servers are independent processes and share no code. The conventions (output shaped for a
model's context window, `req=N` log correlation, gated mutations, the log format) are duplicated
on purpose and will be factored into a library when a third server forces it.

Design documentation is in [`docs/`](docs/) — architecture, the authentication split, the tool
conventions, the logging design, a document per server, and the decision records.

## Getting started

You need the .NET 10 SDK, an Entra ID app registration per server (a public client in your
tenant), and an MCP client such as Claude Code, VS Code or Cursor.

Then, per server:

1. Install it from nuget.org: `dotnet tool install --global JasonBright.Mcp.Teams` /
   `JasonBright.Mcp.AzureDevOps` ([Installing as .NET tools](#installing-as-net-tools)). Or run
   from a checkout with `dotnet run --project src/<Server> --`.
2. Register it in the repository that should use it: `teams-mcp install` / `ado-mcp install`
   ([Registering a server in a repository](#registering-a-server-in-a-repository)).
3. Set the environment variables the registration references. There are no secrets: tenant and
   client ids are OAuth public identifiers.
4. Sign in once: `teams-mcp auth` / `ado-mcp auth`. Interactive, console-only. The server itself
   never prompts.
5. Verify: `teams-mcp selftest` / `ado-mcp selftest`. That runs the same silent-auth path the
   server uses plus one real API round trip, with raw errors on the console.

Each server works on its own. Install only the one you need.

## Teams server

### Configuration

No values live in this repo. Everything comes from the environment:

| Variable | Purpose |
| --- | --- |
| `TEAMS_MCP_TENANT_ID` | Entra tenant id |
| `TEAMS_MCP_CLIENT_ID` | App registration (public client) id |
| `TEAMS_MCP_ALLOW_SEND` | Opt-in gate for the send tools. Anything but `true` refuses sends. It also decides which scopes sign-in asks for (see below) |
| `TEAMS_MCP_AUTH` | `devicecode` (default) or `browser`, used only by `-- auth` |
| `TEAMS_MCP_LOG_LEVEL` | `Trace`/`Debug`/`Information` (default)/`Warning`/`Error`/`None` |
| `TEAMS_MCP_LOG_DIR` | Log directory, default `%LOCALAPPDATA%\teams-mcp\logs` |
| `TEAMS_MCP_LOG_CONTENT` | `true` logs message bodies verbatim, default off |

The app registration must be a public client (device code flow enabled, plus a
`http://localhost` Mobile/Desktop redirect URI for browser mode) with delegated Graph
permissions: `User.Read`, `Team.ReadBasic.All`, `Channel.ReadBasic.All`, `Chat.Read`,
`ChannelMessage.Read.All` (admin consent), and, only if sending will ever be enabled,
`ChannelMessage.Send` and `ChatMessage.Send`.

### Sign-in (once)

```
dotnet run --project src/TeamsMcp -- auth
```

Interactive device-code sign-in. The MSAL token cache persists (OS-protected) with an
authentication record at `%LOCALAPPDATA%\teams-mcp\auth-record.json`, so the server never
prompts. It fails with instructions when sign-in is missing or expired.

The scopes requested follow the send gate. With `TEAMS_MCP_ALLOW_SEND` unset, `auth` asks for
only the read scopes, so a read-only deployment never consents to posting as the signed-in user.
That narrows consent rather than the token: Entra returns every scope already granted to the app,
so reducing the request cannot take a permission back. Turning the gate on later may therefore
need a fresh `-- auth`, and the server says so. It records the granted scopes in
`%LOCALAPPDATA%\teams-mcp\auth-scopes.json` and, when they fall short, fails with an error naming
the missing ones. Both `-- auth` and `-- selftest` print requested next to granted.

### Tools

Read: `list_teams`, `list_channels`, `list_chats`, `read_channel_messages`, `read_chat_messages`,
plus the waiters `wait_for_channel_messages` and `wait_for_chat_messages`. `team` and `channel`
accept ids or display names. `list_chats` filters by member or topic to find a chat id. Message
reads return `{messages, hasMore?, skipped?}`: deleted and system messages are skipped and
counted, bodies are plain text (links kept as `text (url)`), truncated at `body_limit` with
`truncated: true`. Null fields are omitted from all output.

Search, over the Microsoft Search index: `search_messages`, `list_mentions`, and the waiters
`wait_for_mentions` and `wait_for_any_message`. These four are the only tools that span every
chat and every joined team's channels in one request, because Graph has no delegated "all my
messages" endpoint. Two things follow from being index-backed: a hit trails what was just said by
seconds or longer, and it carries the index's summary rather than the message body. A hit is an
address (`chatId`, or `teamId`+`channelId`, plus `webUrl`), and the read tools fetch the text.

`query` is [KQL](https://learn.microsoft.com/en-us/graph/search-concept-chat-messages):
`from:Alice`, `IsMentioned:true`, `hasAttachment:true`, `sent>2026-07-01`, `"exact phrase"`,
`AND`/`OR`/`NOT`. `since` is applied as a day-granular `sent>` scope on the service and exactly
client-side afterwards.

The waiters return as soon as anything newer than their cursor (or `since`, defaulting to now)
arrives. Running out of `timeout_seconds` is a normal answer with `timedOut: true`, and every
wait returns a `nextCursor` to pass back as `cursor`, so resuming needs no bookkeeping. A client
that supports the MCP Tasks extension gets a task handle to poll. One that does not simply
blocks until the tool's own timeout. `wait_for_chat_messages` accepts up to twenty chats in one
call. The search-backed waiters poll no faster than every 20 seconds.

Mutations (require `TEAMS_MCP_ALLOW_SEND=true`): `send_channel_message`, `send_chat_message`.
Both take an optional `format: html` for hyperlinks.

### MCP registration

`teams-mcp install` writes it (see
[Registering a server in a repository](#registering-a-server-in-a-repository)):

```json
"teams": {
  "type": "stdio",
  "command": "teams-mcp",
  "env": {
    "TEAMS_MCP_TENANT_ID": "${TEAMS_MCP_TENANT_ID}",
    "TEAMS_MCP_CLIENT_ID": "${TEAMS_MCP_CLIENT_ID}"
  }
}
```

`TEAMS_MCP_ALLOW_SEND` is not written: the send gate belongs to an environment rather than to a
repository. `--set TEAMS_MCP_ALLOW_SEND=true` puts it in the file anyway.

## Azure DevOps server

### Configuration

| Variable | Purpose |
| --- | --- |
| `ADO_MCP_TENANT_ID` | Entra tenant id |
| `ADO_MCP_CLIENT_ID` | App registration (public client) id |
| `ADO_MCP_ORG_URL` | Organization URL, e.g. `https://dev.azure.com/contoso` |
| `ADO_MCP_PROJECT` | Optional. Default project when a tool's `project` argument is omitted |
| `ADO_MCP_ALLOW_WRITE` | Opt-in gate for the write tools. Anything but `true` refuses writes |
| `ADO_MCP_DEPLOYMENTS` | Optional. Deployment map path, default `%LOCALAPPDATA%\ado-mcp\deployments.json` |
| `ADO_MCP_AUTH` | `devicecode` (default) or `browser`, used only by `-- auth` |
| `ADO_MCP_LOG_LEVEL` | `Trace`/`Debug`/`Information` (default)/`Warning`/`Error`/`None` |
| `ADO_MCP_LOG_DIR` | Log directory, default `%LOCALAPPDATA%\ado-mcp\logs` |
| `ADO_MCP_LOG_CONTENT` | `true` logs descriptions and comment bodies verbatim, default off |

Authentication is against Entra ID, not a personal access token. A PAT is a long-lived bearer
secret that would sit in the MCP client's config, while the refresh token here lives in the
OS-protected cache and follows the organization's conditional-access policy. The app registration
must be a public client with delegated permission to Azure DevOps (`user_impersonation`). Azure
DevOps' own Entra application id, `499b84ac-1321-427f-aa17-267ca6975798`, is the resource being
requested. It is a fixed public identifier and the one hardcoded id in this repo. The
organization must allow Entra access (Organization settings, Security, Policies).

### Sign-in (once)

```
dotnet run --project src/AzureDevOpsMcp -- auth
```

Same split as the Teams server: this is the only interactive flow, and it writes an
authentication record to `%LOCALAPPDATA%\ado-mcp\auth-record.json` beside the MSAL cache. The
server path can never prompt over stdio.

### Tools

Read:

| Tool | Notes |
| --- | --- |
| `list_projects` | |
| `list_repos` | |
| `list_pull_requests` | Whole project when `repo` is omitted |
| `get_pull_request` | Finds the pull request from its id alone, with review threads |
| `list_work_items` | WIQL or filter arguments, see below |
| `get_work_item` | Description, relations, discussion |
| `list_pipelines` | |
| `list_pipeline_runs` | |
| `get_pipeline_run` | Reports each failed task with its stage, job, errors and log tail |
| `wait_for_pipeline_run` | Polls until the run finishes, then reports like `get_pipeline_run`. A timeout returns the run as it stands with `timedOut: true` |
| `search_code` | Needs the Code Search extension, see below |
| `search_work_items` | Full text. `list_work_items` is the structured query |
| `search_wiki` | |
| `deployment_status` | Config-driven, see the deployment map section |

`project`, `repo`, `pipeline` and `team` accept an id or a name. Names match case-insensitively,
exact first then substring, and an ambiguous or unknown name fails with the candidates listed.
`project` defaults to `ADO_MCP_PROJECT`.

`list_work_items` takes either a full `wiql` query or filter arguments (`type`, `state`,
`assigned_to`, `team`, `changed_since`, `title_contains`). When it builds the query itself it
echoes it back in `wiql`, so a filter that matched nothing can be inspected, refined and passed
back in. `team` restricts results to the area paths that team owns.

The three `search_*` tools go through the Azure DevOps Search service on its own host
(`almsearch.dev.azure.com`, derived from `ADO_MCP_ORG_URL`) and take its query syntax:
`AND`/`OR`/`NOT`, wildcards, and inline filters such as `ext:cs` or `class:Foo`. Every result set
carries the service's `total` match count, so an empty list with `total: 0` really means nothing
matched. `search_code` additionally needs the free Code Search extension installed in the
organization. The service scopes a `path` filter to one repository: a TFVC path (`$/Project/...`)
names its own, any other path needs `repo` alongside it.

Bodies are plain text (work item HTML and pull request Markdown both converted, links kept as
`text (url)`), truncated at `body_limit` with `truncated: true`. Deleted and system-generated
comments are filtered out and counted in `skipped`, as are the timeline records
`get_pipeline_run` does not report because they passed. Null fields are omitted, and fields that
merely repeat the common case are nulled: a `wellFormed` project state, a `succeeded` merge
status, an area path equal to the project.

### Mutations

Require `ADO_MCP_ALLOW_WRITE=true`: `update_work_item`, `create_work_item`,
`add_pull_request_comment`. With the gate unset each refuses with instructions before touching
anything.

Only the arguments given are written. Between them the two work item tools reach everything the
read tools report: `title`, `description`, `repro_steps`, `acceptance_criteria`, `state`,
`assigned_to`, `area`, `iteration`, tags, `priority` and the parent link. `update_work_item`
changes exactly the fields passed — the body fields replace what is there, so read the item first
if you mean to extend it — while its `comment` posts to the discussion and
`add_tags`/`remove_tags` merge case-insensitively with the tags already on the item. A work item
has at most one parent, so `parent` replaces whatever it is under (asking for the parent it
already has writes nothing) and `remove_parent` leaves it unparented. `priority` is the process's
own scale, commonly 1–4; leaving it off a `create_work_item` call takes the process default rather
than a considered value, which is usually 2. `assigned_to` takes a display name, an email, or an
identity GUID. A display name resolves through the identity service, and on a write an ambiguous
name is an error listing the candidates, never a guess. `type` resolves against the project's own
work item types.
`add_pull_request_comment` starts a new thread, or replies on one when `thread_id` (from
`get_pull_request`) is given. Every write returns the post-write state, so no follow-up read is
needed. Deleting things, voting on or completing pull requests, and triggering pipelines are not
offered.

### Deployment map (`deployment_status`)

`deployment_status` answers "what is in production" per deployable. A deployable names either a
classic release pipeline (`releaseDefinition` + `environment`) or a build/YAML pipeline
(`pipeline`, optionally through an ADO Environment named in `environment`, optionally pinned to a
`branch`). Each reports the latest succeeded deployment, the build it shipped, the version that
build was made from (a TFVC changeset or a git commit, whichever the build's repository implies),
and the work landed since: changesets under the deployable's paths, or commits on its branch.
Ask with `changeset: N` and every TFVC-built deployable also answers whether that changeset is
deployed (`containsChangeset`) and whether it touched the deployable's paths (`affects`).

The map is data the server reads from `%LOCALAPPDATA%\ado-mcp\deployments.json` (or
`ADO_MCP_DEPLOYMENTS`), re-read when its timestamp changes:

```json
{
  "deployables": [
    { "name": "clients-website",
      "releaseDefinition": "Clients - Website",
      "environment": "Production",
      "paths": ["$/Contoso/Websites/Trunk"],
      "note": "customer portal" },
    { "name": "billing-api",
      "pipeline": "Billing API",
      "environment": "production",
      "branch": "main" }
  ]
}
```

Names resolve leniently like everywhere else. `paths` are TFVC server-path prefixes, optional
because the server can derive them from the build definition's own TFVC workspace mappings.
Unknown fields are ignored, so other consumers can share the file.

`dotnet run --project src/AzureDevOpsMcp -- config` loads and validates the data files without
driving the tools through an MCP client.

### MCP registration

`ado-mcp install` writes:

```json
"azuredevops": {
  "type": "stdio",
  "command": "ado-mcp",
  "env": {
    "ADO_MCP_TENANT_ID": "${ADO_MCP_TENANT_ID}",
    "ADO_MCP_CLIENT_ID": "${ADO_MCP_CLIENT_ID}",
    "ADO_MCP_ORG_URL": "https://dev.azure.com/contoso",
    "ADO_MCP_PROJECT": "Core"
  }
}
```

The organization and project come from the environment the install ran in and are written
literally, since pinning a repository to its organization is the point. `ADO_MCP_ALLOW_WRITE` and
the data-file paths are not written (`--set` adds them if you want).

## Logs

Each server writes to its own file, `%LOCALAPPDATA%\teams-mcp\logs\teams-mcp.log` and
`%LOCALAPPDATA%\ado-mcp\logs\ado-mcp.log`, and to stderr, which MCP clients usually discard. One
line per event, flushed immediately, rolling to `.1` at 8 MB:

```
2026-07-28T07:03:12.441Z INF 30724 tool.start req=7 read_channel_messages team="Platform" channel="General" limit=20
2026-07-28T07:03:12.902Z DBG 30724 graph.http req=7 GET /v1.0/teams/{id}/channels/{id}/messages -> 200 ms=431 request-id="298a99a3-…"
2026-07-28T07:03:12.905Z INF 30724 tool.ok req=7 read_channel_messages ok ms=464 messages=20 hasMore=true skipped.system=3
```

`req=N` ties a tool call to every HTTP request it made, and errors returned to the model carry
the matching `req=N` plus the log path. Event names to grep: `startup`, `auth.record`,
`auth.mismatch`, `auth.token`, `auth.fail`, `tool.start`, `tool.ok`, `tool.fail`, `resolve`,
`page`, `poll`, `config`, `crash`, and the HTTP pair (`graph.http`/`graph.http.fail` in Teams,
`http`/`http.fail` in Azure DevOps).

User-authored text is not logged unless `…_LOG_CONTENT=true`, only its length. `…_LOG_LEVEL=Debug`
adds successful HTTP calls, paging and name resolution. `Trace` adds the MCP SDK's JSON-RPC
traffic.

Fastest triage, in order: `-- selftest` (separates auth problems from tool problems), the
`startup` lines at the top of the log, then the failing HTTP line with the service's own error
body.

## Cross-platform notes

Both servers run on Windows, Linux and macOS. `%LOCALAPPDATA%` in this README means .NET's
local-application-data folder wherever the platform puts it: `$XDG_DATA_HOME` (default
`~/.local/share`) on Linux, `~/Library/Application Support` on macOS.

The MSAL token cache is encrypted with whatever the OS provides: DPAPI on Windows, the Keychain
on macOS, libsecret on Linux. On headless Linux with no keyring, `auth` fails with a
cache-persistence error. The servers do not opt into the unencrypted-file fallback, so provide a
keyring rather than working around it. Sign-in itself works over SSH: the device-code flow only
needs a console here and a browser somewhere.

## Installing as .NET tools

Both servers are on nuget.org. Install either or both:

```powershell
dotnet tool install --global JasonBright.Mcp.Teams
dotnet tool install --global JasonBright.Mcp.AzureDevOps
```

| Package | Tool command | Server |
| --- | --- | --- |
| [`JasonBright.Mcp.Teams`](https://www.nuget.org/packages/JasonBright.Mcp.Teams) | `teams-mcp` | Teams, through Microsoft Graph |
| [`JasonBright.Mcp.AzureDevOps`](https://www.nuget.org/packages/JasonBright.Mcp.AzureDevOps) | `ado-mcp` | Azure DevOps |

That puts `teams-mcp` and `ado-mcp` on the PATH, and all the verbs work as they do from source.
`dotnet tool update --global <id>` moves an installation to the latest release; both packages ship
one shared version, so they move together.

The ids are owner-prefixed because `AzureDevOpsMcp`, `AdoMcp` and `AdoMcpServer` are all taken on
nuget.org by unrelated packages, and nuget.org rejects a new id that differs from an existing one
only by case or separators — which ruled out `Ado.Mcp` and friends too. The ids are independent of
the assembly names and of the tool commands, so none of those move with them.

### Installing a local build

To run a change that has not been released, pack it and install from `artifacts/`:

```powershell
dotnet tool restore                                     # once per clone, puts nbgv on `dotnet nbgv`
dotnet pack
$v = dotnet nbgv get-version -v NuGetPackageVersion     # e.g. 0.1.3, or 0.1.3-g1a2b3c4d5e off main
dotnet tool install --global --add-source ./artifacts --version $v JasonBright.Mcp.Teams
dotnet tool install --global --add-source ./artifacts --version $v JasonBright.Mcp.AzureDevOps
```

The version pin is what makes that work: `--add-source` adds `artifacts/` to your feeds rather than
replacing them, so NuGet still resolves the highest version across all of them — which is the
release on nuget.org, not the build you just made, and it reports success either way. A rebuild at
the same version is not picked up by `dotnet tool update` either (it sees the version already
satisfied), so uninstall and reinstall. `scripts/rebuild.ps1` does all of this for both servers,
against a generated config with every source cleared but `artifacts/`, and verifies the swap
actually happened.

### How the packages are published

Dispatching `.github/workflows/release.yml` from `main` is the whole of it: it packs, tags, creates
the GitHub Release, and pushes both `.nupkg` files to nuget.org.

There is no long-lived API key. The push uses
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the job
mints a GitHub OIDC token (`id-token: write` in its `permissions:`), `NuGet/login@v1` exchanges it
with nuget.org for a key that lives one hour, and the push uses that. What authorizes the exchange
is a policy registered on nuget.org naming the repository owner, the repository, and the workflow
**file name** — `release.yml`, without the `.github/workflows/` path. Two things follow:

- **Renaming or moving that file breaks publishing** until the policy is edited to match. The policy
  matches the file name, not the job or the step inside it.
- **A policy covers every package its owner owns**, so a third server publishes with nothing issued
  and nothing changed here. The `JasonBright.` prefix is reserved on nuget.org, so nobody else can
  claim an id under it either.

One repository secret exists, `NUGET_USER`: the nuget.org profile name (not an email) that
`NuGet/login@v1` takes. It is an identifier rather than a credential — publishing is authorized by
the OIDC token and the policy behind it.

**A policy on a private repository is provisional for its first seven days.** nuget.org cannot bind
it to GitHub's immutable repository and owner ids until a publish arrives carrying them, and that
binding is what stops someone deleting a repository, recreating it under the same name, and
inheriting the right to publish. The policy works normally during the window; if nothing is
published within it the policy goes inactive, and the window can be restarted from the Trusted
Publishing page as often as needed. The first successful push makes it permanent. This repository is
private, so that applied to the first release and will apply again to any policy added later.

The metadata behind the packages, in case a new one is added:

- **License** is MIT — `PackageLicenseExpression` in `Directory.Build.props`, `LICENSE` at the root.
- **Package ids and tool command names** are per-csproj, since they are what make each package a
  distinct tool. Everything shared — authors, product, tags, license, output path — is in
  `Directory.Build.props`.
- **`RepositoryUrl` stays unset** while the remote is private: it would 404 for every consumer and
  SourceLink would resolve to nothing. Set it if the repository goes public.

**A push cannot be undone.** A published version can never be re-pushed or deleted, only unlisted.
That is why the release workflow refuses any ref that is not `main`, any version nbgv calls a
prerelease, and any tag that already exists — all before it builds anything.

## Registering a server in a repository

```powershell
ado-mcp install            # in the repository the server should be available in
teams-mcp install
```

Installing finds the repository (walking up to the nearest `.git`), detects which MCP client it
uses, and merges the server into that client's config:

| Client | Config written | Servers property | Reference syntax | Detected from |
| --- | --- | --- | --- | --- |
| `claude` | `.mcp.json` | `mcpServers` | `${VAR}` | `.mcp.json`, `.claude/`, `CLAUDE.md` |
| `vscode` | `.vscode/mcp.json` | `servers` | `${env:VAR}` | `.vscode/mcp.json`, `.github/copilot-instructions.md` |
| `cursor` | `.cursor/mcp.json` | `mcpServers` | `${VAR}` | `.cursor/mcp.json`, `.cursorrules` |

When several clients are detected the first wins and the others are named in the output.
`--client <name>` picks one, and a repository showing signs of nothing gets the Claude Code
shape.

Identity is referenced (`${ADO_MCP_TENANT_ID}`), addresses are literal, and mutation gates are
never written. Nothing already in the file is lost: other servers and top-level properties are
preserved, and an entry that already differs is a refusal printing both versions until `--force`.
Re-running with the same environment is a no-op. The written command reflects how the install was
invoked: the .NET tool registers `ado-mcp`, a checkout registers `dotnet run --project <path>`.

```
ado-mcp install [directory] [options]

  --client <name>    claude | vscode | cursor (default: whichever the repository shows signs of)
  --file <path>      write this file instead of the client's own config path
  --name <key>       key for this server in the config (default: azuredevops / teams)
  --set KEY=VALUE    add or override an env entry, KEY= removes one, repeatable
  --force            replace an existing entry that differs
  --dry-run          print the resulting file, write nothing
```

Installing only writes the registration, so it ends by reporting which referenced variables are
missing from the current environment and whether sign-in has happened. `install`, then `auth`,
then `selftest`.

## Versioning

The version comes from
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning). `version.json`
carries `major.minor`, nbgv derives the patch from git height (commits since that line last
changed), and no csproj sets a version.

```powershell
dotnet tool restore              # once per clone
dotnet nbgv get-version          # what this checkout would ship as
```

An ordinary commit needs no version edit. Bump `version.json` only for a new major/minor. Only
`main` produces a clean version (`0.1.4`). Everywhere else builds a `-g<commit>` prerelease, so a
branch build cannot be mistaken for a release. Anything computing a version needs full git
history, which is why both workflows check out with `fetch-depth: 0`.

## Continuous integration

Two GitHub Actions workflows, both on `windows-latest` (the servers are Windows-first: DPAPI
token cache, `install`'s client detection). Nothing in a build or a test reaches Graph or Azure
DevOps, so neither workflow carries a credential: the one repository secret, `NUGET_USER`, is a
nuget.org profile name, and the key that publishes is minted per run by trusted publishing.

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `.github/workflows/pr.yml` | pull request against `main`, push to `main` | Builds the solution in Release with `-warnaserror`, runs the tests, and writes the computed version and test counts into the job summary |
| `.github/workflows/release.yml` | manual dispatch from `main` | Cuts a release, see below |

To cut a release, run the release workflow from `main` (bump `version.json` first only for a new
major/minor). It refuses if the ref is not `main`, if nbgv calls the build a prerelease, or if
the tag already exists. Then it builds, tests, packs, tags with `nbgv tag` (`v0.1.4`), pushes the
tag, publishes a GitHub Release with both `.nupkg` files attached, and pushes both packages to
nuget.org through trusted publishing — no API key is stored anywhere (see
[How the packages are published](#how-the-packages-are-published)).

## Working on this repo

`.mcp.json` registers a C# language server as an MCP server so an agent editing this code can
resolve symbols instead of grepping. The tools it launches are not checked in, so install them
once:

```powershell
dotnet tool install --global csharp-ls
dotnet tool install --global CSharpLspMcp
```

Call `csharp_set_workspace` with the solution or project path before the other tools work.
