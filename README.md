# mcp-dotnet

Two MCP stdio servers on .NET 10, built on the official
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK, signing in to
Entra ID through Azure.Identity.

- **Teams** (`teams-mcp`): Microsoft Teams via Microsoft Graph. Read teams, channels, chats and
  messages, search, wait for new messages, and send behind an opt-in gate.
- **Azure DevOps** (`ado-mcp`): projects, repositories, pull requests, work items, pipelines and
  search (code, work items, wiki) via the REST API, plus gated work item writes and pull request
  comments.

Each server works on its own, so install only the one you need. Design documentation is in
[`docs/`](docs/).

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and an Entra ID app registration
in your own tenant, described under [App registrations](#app-registrations): the servers sign in as
you against your own organization, so there is no shared id to hand out.

**1. Install the servers** from nuget.org. This puts `teams-mcp` and `ado-mcp` on your PATH.

```powershell
dotnet tool install --global JasonBright.Mcp.Teams
dotnet tool install --global JasonBright.Mcp.AzureDevOps
```

**2. Point them at your tenant.** Tenant and client ids are public OAuth identifiers, not secrets.
Set them at user scope and every client picks them up: an MCP server is a child process of the
client that launches it.

```powershell
[Environment]::SetEnvironmentVariable('TEAMS_MCP_TENANT_ID', '<tenant-guid>',     'User')
[Environment]::SetEnvironmentVariable('TEAMS_MCP_CLIENT_ID', '<app-guid>',        'User')
[Environment]::SetEnvironmentVariable('ADO_MCP_TENANT_ID',   '<tenant-guid>',     'User')
[Environment]::SetEnvironmentVariable('ADO_MCP_CLIENT_ID',   '<app-guid>',        'User')
[Environment]::SetEnvironmentVariable('ADO_MCP_ORG_URL',     'https://dev.azure.com/contoso', 'User')
[Environment]::SetEnvironmentVariable('ADO_MCP_PROJECT',     'Core',              'User')
```

On Linux and macOS these are `export` lines in your shell profile. Terminals that are already open
keep the old values, so start a new one.

Both servers are read-only until you also set a gate; see [Enabling writes](#enabling-writes).
Every other variable has a default, listed in each server's section below.

**3. Install the plugin** in Claude Code. It registers both servers and brings four skills that
use them (`teams-message`, `teams-followup`, `teams-watcher`, `mcp-reauth`).

```
/plugin marketplace add jgbright/mcp-dotnet
/plugin install mcp-dotnet@mcp-dotnet
```

On VS Code or Cursor, or to keep the config in one repository, write it yourself
([a `.mcp.json` by hand](#a-mcpjson-by-hand)) or let the server write it
([Registering a server in a repository](#registering-a-server-in-a-repository)).

**4. Sign in once, then check it.** Sign-in is interactive and console-only; the server has nowhere
to prompt over stdio.

```powershell
teams-mcp auth       # device-code sign-in, once per machine
teams-mcp selftest   # silent-auth path plus one real Graph call, raw errors on the console

ado-mcp auth
ado-mcp selftest
```

Restart the MCP client and the tools are there. If something is wrong, run `selftest` first: it
tells you whether the problem is sign-in or the tool. See [Logs](#logs) for the rest of the triage.

### Calling a tool from the command line

`call` invokes one tool per run with no MCP client on the other end. The server is the real one,
driven over in-memory pipes, so a call that works here works under MCP. Result JSON is the only
thing on stdout (logs go to stderr and the log file) and a tool error exits non-zero, so output
pipes into `ConvertFrom-Json` or `jq`.

```powershell
ado-mcp call                                       # no tool: list the tools
ado-mcp call list_repos project=Core limit=5       # KEY=VALUE pairs, coerced by the tool's schema
ado-mcp call list_repos '{"project":"Core"}'       # or one JSON object
'{"project":"Core"}' | ado-mcp call list_repos -   # or that object on stdin, if quoting fights back
teams-mcp call list_teams
```

Tool names resolve leniently, as they do inside the tools: `call repos` finds `list_repos` and
notes the correction on stderr, an ambiguous or unknown name fails listing the candidates, and a
wrong argument fails before anything is sent, naming what the tool takes.

### Enabling writes

Both servers are read-only out of the box. The mutating tools are always listed and refuse at call
time until their gate is set.

| Gate | Enables |
| --- | --- |
| `TEAMS_MCP_ALLOW_SEND=true` | `send_channel_message`, `send_chat_message`, `react_to_channel_message`, `react_to_chat_message` |
| `ADO_MCP_ALLOW_WRITE=true` | `create_work_item`, `update_work_item`, `add_pull_request_comment`, `run_pipeline`, `deploy_release`, and any method other than GET or HEAD through `ado_api_request` |
| `ADO_MCP_ALLOW_APPROVE=true` | `approve_release`, and only alongside `ADO_MCP_ALLOW_WRITE=true` |

Set them the same way as step 2, or in a hand-written config's `env` block.

- Teams needs a fresh `teams-mcp auth` afterward. The scopes sign-in asks for follow the gate, so a
  token minted while the gate was off carries no permission to post ([Sign-in](#sign-in-once)).
  Azure DevOps uses a single resource scope, so its gate is only a call-time refusal.
- Neither `install` nor the plugin writes a gate. That config usually gets committed, and whether a
  machine may post as you is not a property of a repository.

### A `.mcp.json` by hand

With the plugin, configuration stays in your environment and the file is empty. To keep it in one
place, or on a client the plugin does not cover, this is the whole file with both gates on:

```json
{
  "mcpServers": {
    "teams": {
      "type": "stdio",
      "command": "teams-mcp",
      "env": {
        "TEAMS_MCP_TENANT_ID": "00000000-0000-0000-0000-000000000000",
        "TEAMS_MCP_CLIENT_ID": "11111111-1111-1111-1111-111111111111",
        "TEAMS_MCP_ALLOW_SEND": "true"
      }
    },
    "azuredevops": {
      "type": "stdio",
      "command": "ado-mcp",
      "env": {
        "ADO_MCP_TENANT_ID": "00000000-0000-0000-0000-000000000000",
        "ADO_MCP_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
        "ADO_MCP_ORG_URL": "https://dev.azure.com/contoso",
        "ADO_MCP_PROJECT": "Core",
        "ADO_MCP_ALLOW_WRITE": "true"
      }
    }
  }
}
```

Put it at the root of a repository for Claude Code. VS Code wants the same entries at
`.vscode/mcp.json` under a `servers` property, using `${env:VAR}` as its reference syntax. Cursor
wants `.cursor/mcp.json`, shaped like Claude Code's.

This file usually gets committed, so mind what goes in it literally. `"${TEAMS_MCP_TENANT_ID}"` is
legal anywhere a literal is, and referencing identity lets one file serve a team spread across
tenants; addresses are the part worth writing literally, the organization URL and the default
project. A literal gate hands everyone who clones the repository the ability to post as themselves.
That can be what you want on a personal machine, and rarely is on a shared one.

### App registrations

One public-client app registration per server, in your tenant. Public client because these are
desktop tools with nowhere to keep a secret.

**Teams** needs these delegated Microsoft Graph permissions: `User.Read`, `Team.ReadBasic.All`,
`Channel.ReadBasic.All`, `Chat.Read`, and `ChannelMessage.Read.All` (admin consent). Add
`ChannelMessage.Send` and `ChatMessage.Send` only if sending will ever be enabled. Enable the
device code flow. Add a `http://localhost` Mobile/Desktop redirect URI if you want
`TEAMS_MCP_AUTH=browser`.

**Azure DevOps** needs delegated permission to Azure DevOps (`user_impersonation`). It is a single
resource scope, so there is no scope list to maintain. The organization also has to allow Entra
access, under Organization settings, Security, Policies.

One registration can serve both: each server reads only its own two variables, so pointing them at
the same app id works.

## Teams server

### Configuration

Everything comes from the environment; no values live in this repo.

| Variable | Purpose |
| --- | --- |
| `TEAMS_MCP_TENANT_ID` | Entra tenant id |
| `TEAMS_MCP_CLIENT_ID` | App registration (public client) id |
| `TEAMS_MCP_ALLOW_SEND` | Opt-in gate for the send tools. Anything but `true` refuses sends. It also decides which scopes sign-in asks for (see below) |
| `TEAMS_MCP_AUTH` | `devicecode` (default) or `browser`, used only by `-- auth` |
| `TEAMS_MCP_LOG_LEVEL` | `Trace`/`Debug`/`Information` (default)/`Warning`/`Error`/`None` |
| `TEAMS_MCP_LOG_DIR` | Log directory, default `%LOCALAPPDATA%\teams-mcp\logs` |
| `TEAMS_MCP_LOG_CONTENT` | `true` logs message bodies verbatim, default off |

[App registrations](#app-registrations) says what the app registration needs.

### Sign-in (once)

```
dotnet run --project src/TeamsMcp -- auth
```

Interactive device-code sign-in. The OS-protected MSAL token cache persists, with an authentication
record at `%LOCALAPPDATA%\teams-mcp\auth-record.json`, which is why the server never prompts.
Missing or expired sign-in fails with instructions.

The scopes requested follow the send gate. With `TEAMS_MCP_ALLOW_SEND` unset, `auth` asks only for
the read scopes, so a read-only install never consents to posting as you. That narrows the consent,
not the token: Entra returns every scope already granted to the app, so asking for less cannot take
a permission back. Turning the gate on later may need a fresh `-- auth`, and the server says so.
Granted scopes are recorded in `%LOCALAPPDATA%\teams-mcp\auth-scopes.json`, and when they fall short
the error names the missing ones. `-- auth` and `-- selftest` both print requested next to granted.

### Tools

Read: `list_teams`, `list_channels`, `list_chats`, `read_channel_messages`, `read_chat_messages`,
`download_message_images`, plus the waiters `wait_for_channel_messages` and
`wait_for_chat_messages`.

`team` and `channel` accept ids or display names. `list_chats` filters by member or topic to find a
chat id. Message reads return `{messages, hasMore?, skipped?}`; deleted and system messages are
skipped and counted. Bodies come back as plain text, links kept as `text (url)`, truncated at
`body_limit` and flagged with `truncated: true`. Null fields are omitted everywhere.

Search, over the Microsoft Search index: `search_messages`, `list_mentions`, and the waiters
`wait_for_mentions` and `wait_for_any_message`. These four are the only tools that span every chat
and every joined team's channels in one request, because Graph has no delegated "all my messages"
endpoint.

An index hit trails what was just said by seconds or longer, and carries the index's summary instead
of the message body. Treat a hit as an address (`chatId`, or `teamId` plus `channelId`, plus
`webUrl`) and use the read tools to fetch the text.

`query` is [KQL](https://learn.microsoft.com/en-us/graph/search-concept-chat-messages):
`from:Alice`, `IsMentioned:true`, `hasAttachment:true`, `sent>2026-07-01`, `"exact phrase"`,
`AND`/`OR`/`NOT`. `since` is applied as a day-granular `sent>` scope on the service, then applied
exactly client-side afterwards.

The waiters return as soon as anything newer than their cursor arrives. The cursor defaults to
`since`, which defaults to now. Running out of `timeout_seconds` is a normal answer with
`timedOut: true`. Every wait returns a `nextCursor` to pass back as `cursor`, so resuming needs no
bookkeeping. A client that supports the MCP Tasks extension gets a task handle to poll; one that
does not blocks until the tool's own timeout. `wait_for_chat_messages` accepts up to twenty chats in
one call. The search-backed waiters poll no faster than every 20 seconds.

Mutations, gated on `TEAMS_MCP_ALLOW_SEND=true`: `send_channel_message`, `send_chat_message`,
`react_to_channel_message` and `react_to_chat_message`. The send tools take the message as `body`,
the same word the reads use, plus an optional `format`: `markdown` for anything with structure
(converted server-side), `html` only for markup markdown cannot express. The reaction tools set or
clear one emoji on a message and ride the same send scopes.

### MCP registration

`teams-mcp install` writes this. See
[Registering a server in a repository](#registering-a-server-in-a-repository).

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

`TEAMS_MCP_ALLOW_SEND` is not written: the send gate belongs to an environment, not to a repository.
`--set TEAMS_MCP_ALLOW_SEND=true` puts it in the file anyway.

## Azure DevOps server

### Configuration

| Variable | Purpose |
| --- | --- |
| `ADO_MCP_TENANT_ID` | Entra tenant id |
| `ADO_MCP_CLIENT_ID` | App registration (public client) id |
| `ADO_MCP_ORG_URL` | Organization URL, e.g. `https://dev.azure.com/contoso` |
| `ADO_MCP_PROJECT` | Optional. Default project when a tool's `project` argument is omitted |
| `ADO_MCP_ALLOW_WRITE` | Opt-in gate for the write tools. Anything but `true` refuses writes |
| `ADO_MCP_ALLOW_APPROVE` | Second opt-in gate, for `approve_release` only. Acting on a release approval needs this *and* `ADO_MCP_ALLOW_WRITE` |
| `ADO_MCP_DEPLOYMENTS` | Optional. Deployment map path, default `%LOCALAPPDATA%\ado-mcp\deployments.json` |
| `ADO_MCP_AUTH` | `devicecode` (default) or `browser`, used only by `-- auth` |
| `ADO_MCP_LOG_LEVEL` | `Trace`/`Debug`/`Information` (default)/`Warning`/`Error`/`None` |
| `ADO_MCP_LOG_DIR` | Log directory, default `%LOCALAPPDATA%\ado-mcp\logs` |
| `ADO_MCP_LOG_CONTENT` | `true` logs descriptions and comment bodies verbatim, default off |

Authentication goes through Entra ID, not a personal access token: a PAT is a long-lived bearer
secret that would sit in the MCP client's config, while the refresh token here lives in the
OS-protected cache and follows the organization's conditional-access policy.
[App registrations](#app-registrations) says what the app registration needs.

Azure DevOps' own Entra application id, `499b84ac-1321-427f-aa17-267ca6975798`, is the resource
being requested. It is a fixed public identifier and the one hardcoded id in this repo.

### Sign-in (once)

```
dotnet run --project src/AzureDevOpsMcp -- auth
```

Same split as the Teams server. This is the only interactive flow; it writes an authentication
record to `%LOCALAPPDATA%\ado-mcp\auth-record.json` beside the MSAL cache, and the server path can
never prompt over stdio.

### Tools

Read:

| Tool | Notes |
| --- | --- |
| `list_projects` | |
| `list_repos` | |
| `list_pull_requests` | Whole project when `repo` is omitted |
| `get_pull_request` | Finds the pull request from its id alone, with review threads |
| `wait_for_pull_request` | Polls until the pull request completes or is abandoned, then reports like `get_pull_request`. A timeout returns it as it stands with `timedOut: true` |
| `list_work_items` | WIQL or filter arguments, see below |
| `get_work_item` | Description, repro steps and acceptance criteria; relations and discussion on request |
| `list_pipelines` | |
| `list_pipeline_runs` | |
| `get_pipeline_run` | Reports each failed task with its stage, job, errors and log tail |
| `wait_for_pipeline_run` | Polls until the run finishes, then reports like `get_pipeline_run`. A timeout returns the run as it stands with `timedOut: true` |
| `list_release_definitions` | Classic release pipelines, with the environments each deploys to |
| `get_release_definition` | How one is configured: variables at both scopes, variable groups, and each environment's tasks with their inputs and the deployment group and tags each phase targets |
| `get_release_definition_targets` | Where each stage lands: its deployment group and the machines its tags select right now. An empty `machines` list is a stage that would deploy to nothing |
| `list_deployment_groups` | The machines classic release stages deploy to, with their tags and agent status. Not the Environments YAML pipelines use |
| `search_release_definitions` | Where a name or value appears across every definition: in a variable, a task input, or both |
| `list_releases` | Releases of one definition, newest first, with every environment's status |
| `get_release` | Artifacts, pending approvals, and each failed task with its phase, job, errors and log tail; `include_tasks` lists every task and `task_log` fetches one's log |
| `wait_for_release` | Polls one environment until it stops moving, then reports like `get_release` |
| `search_code` | Needs the Code Search extension, see below |
| `search_work_items` | Full text. `list_work_items` is the structured query |
| `search_wiki` | |
| `deployment_status` | Config-driven, see the deployment map section |
| `ado_api_request` | One REST call this server has no typed tool for, on its own credential and its own organization only. A JSON Patch body is sent as `application/json-patch+json`, which is the only type the work item endpoints take; `content_type` overrides that |
| `ado_auth_status` | Which credential is in use, whether it still works, and whether `AZURE_DEVOPS_PAT` does |

`project`, `repo`, `pipeline`, `team`, `definition` and `environment` accept an id or a name,
matched case-insensitively, exact first then substring; an ambiguous or unknown name fails with the
candidates listed. `project` defaults to `ADO_MCP_PROJECT`.

Azure DevOps has two kinds of pipeline that share nothing, and the `_pipeline` and `_release` tools
keep them apart. `list_pipelines` returns build/YAML pipelines; `list_release_definitions` returns
classic release pipelines. A release definition has environments (stages), a release is one instance
of it, and each environment deploys separately. A release definition never appears in
`list_pipelines`.

A release environment has no `failed` status. **A deployment that failed reports as `rejected`**,
the same status a turned-down approval produces; `operationStatus` (`PhaseFailed` against
`Rejected`) tells them apart, and `get_release` reports both.

A release says what a deploy *did*; a release definition says what it is *set up to do*.
`get_release_definition` answers the second: whether a pipeline overrides a setting at deploy time,
and which files its substitution tasks rewrite, which decides whether editing a checked-in config
file changes anything in production. `search_release_definitions` asks that across every definition
in the project. Neither returns a value Azure DevOps marks secret: the name and `isSecret: true` are
the answer, through `ado_api_request` too.

Where a stage *lands* is a third question. `get_release_definition_targets` resolves each stage to
the machines its deployment group and tags select: all of the tags, case-insensitively, and no tags
meaning every machine in the group. A stage named "Staging" that targets production servers, or tags
that match nothing, is visible before the deploy. Agent capabilities (the agent's environment
variables) are never returned: Azure DevOps does not mark them secret, and they carry keys.

`list_work_items` takes a full `wiql` query or filter arguments (`type`, `state`, `assigned_to`,
`team`, `changed_since`, `title_contains`). A query it builds itself is echoed back in `wiql`, so a
filter that matched nothing can be inspected, refined and passed back in. `team` restricts results
to the area paths that team owns.

The three `search_*` tools go through the Azure DevOps Search service on its own host,
`almsearch.dev.azure.com`, derived from `ADO_MCP_ORG_URL`. They take its query syntax:
`AND`/`OR`/`NOT`, wildcards, and inline filters like `ext:cs` or `class:Foo`. Every result set
carries the service's `total` match count, so an empty list with `total: 0` really does mean nothing
matched.

`search_code` also needs the free Code Search extension installed in the organization. The service
scopes a `path` filter to one repository. A TFVC path (`$/Project/...`) names its own. Any other
path needs `repo` alongside it.

Bodies come back as plain text. Work item HTML and pull request Markdown are both converted, links
kept as `text (url)`, long bodies truncated at `body_limit` and flagged with `truncated: true`.
Deleted and system-generated comments are filtered out and counted in `skipped`, as are the timeline
records `get_pipeline_run` does not report because they passed. Null fields are omitted, and fields
that just repeat the common case are nulled out: a `wellFormed` project state, a `succeeded` merge
status, an area path equal to the project.

### Mutations

`update_work_item`, `create_work_item`, `add_pull_request_comment`, `run_pipeline` and
`deploy_release` require `ADO_MCP_ALLOW_WRITE=true`. With the gate unset, each refuses with
instructions before touching anything.

`approve_release` requires **both** `ADO_MCP_ALLOW_WRITE=true` and `ADO_MCP_ALLOW_APPROVE=true`,
because the two are not the same permission. Writing says an agent may change what other people see.
An approval is a control that exists to require a human, and answering one records *you* as having
authorized that deployment whether or not you read what was in it. Leave `ADO_MCP_ALLOW_APPROVE`
unset unless you mean it.

Only the arguments you pass are written. Between them the two work item tools reach everything the
read tools report: `title`, `description`, `repro_steps`, `acceptance_criteria`, `state`,
`assigned_to`, `area`, `iteration`, tags, `priority`, the estimates and the parent link.

`update_work_item` changes exactly the fields passed. The body fields replace what is there, so read
the item first if you mean to extend it. Its `comment` posts to the discussion; `add_tags` and
`remove_tags` merge case-insensitively with the tags already on the item.

A work item has at most one parent, so `parent` replaces whatever it is under; asking for the parent
it already has writes nothing. `remove_parent` leaves it unparented.

`priority` is the process's own scale, commonly 1 to 4. Left off a `create_work_item` call it takes
the process default, not a considered value, usually 2.

The estimates are five separate fields because the processes spell the same idea differently:
`original_estimate`, `remaining_work` and `completed_work` are hours on a Task, `story_points` is
the Agile User Story field, and `effort` is the Scrum one. A work item type defines some and not
others, and writing one it does not define is refused by Azure DevOps naming the field. Nothing
couples them: `original_estimate` does not set `remaining_work`, and a sprint burndown reads the
second, so set both when you are starting from an estimate.

`assigned_to` takes a display name, an email, or an identity GUID. A display name resolves through
the identity service. On a write, an ambiguous name is an error listing the candidates, never a
guess. `type` resolves against the project's own work item types.

`add_pull_request_comment` starts a new thread, or replies on one when you pass a `thread_id` from
`get_pull_request`.

`run_pipeline` queues a run, optionally on a specific branch, and returns it with the id
`wait_for_pipeline_run` takes, so a delivery flow chains one call at a time: wait for the PR to
land, kick CI, watch it, kick the follow-on build.

`deploy_release` starts deploying one environment of an existing release: the Deploy button, not a
new release. It does not create releases, so pass the id of one that already exists, from
`list_releases`. If the environment has a pre-deploy approval the deployment waits at it instead of
starting, and `get_release` reports that as a `pendingApprovals` entry carrying the id
`approve_release` takes. `approve_release` refuses rather than guessing when an environment has no
pending approval, or more than one; pass `approval_id` to pick. Both return the release in
`get_release`'s shape.

Every write returns the state after the write, so you never need a follow-up read. Deleting things,
and voting on or completing pull requests, are not offered.

### Deployment map (`deployment_status`)

`deployment_status` answers "what is in production" for each deployable. A deployable names either a
classic release pipeline (`releaseDefinition` plus `environment`) or a build or YAML pipeline
(`pipeline`, optionally through an ADO Environment named in `environment`, optionally pinned to a
`branch`).

Each one reports the latest succeeded deployment, the build it shipped, the version that build was
made from (a TFVC changeset or a git commit, whichever the build's repository implies), and the work
landed since: changesets under the deployable's paths, or commits on its branch.

Ask with `changeset: N` and every TFVC-built deployable also answers whether that changeset is
deployed (`containsChangeset`) and whether it touched the deployable's paths (`affects`).

The map is data, read from `%LOCALAPPDATA%\ado-mcp\deployments.json` or from `ADO_MCP_DEPLOYMENTS`,
and re-read whenever the file's timestamp changes.

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

Names resolve leniently, as everywhere else. `paths` are TFVC server-path prefixes and are optional:
the server can derive them from the build definition's own TFVC workspace mappings. Unknown fields
are ignored, so other tools can share the file.

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

The organization and project come from the environment the install ran in and are written literally,
since pinning a repository to its organization is the point. `ADO_MCP_ALLOW_WRITE` and the data-file
paths are not written; `--set` adds them.

## Logs

Each server writes to its own file, `%LOCALAPPDATA%\teams-mcp\logs\teams-mcp.log` and
`%LOCALAPPDATA%\ado-mcp\logs\ado-mcp.log`, and to stderr, which MCP clients usually discard. One
line per event, flushed immediately, rolling to `.1` at 8 MB.

```
2026-07-28T07:03:12.441Z INF 30724 tool.start req=7 read_channel_messages team="Platform" channel="General" limit=20
2026-07-28T07:03:12.902Z DBG 30724 graph.http req=7 GET /v1.0/teams/{id}/channels/{id}/messages -> 200 ms=431 request-id="298a99a3-…"
2026-07-28T07:03:12.905Z INF 30724 tool.ok req=7 read_channel_messages ok ms=464 messages=20 hasMore=true skipped.system=3
```

`req=N` ties a tool call to every HTTP request it made. Errors returned to the model carry the
matching `req=N` plus the log path.

Event names to grep for: `startup`, `auth.record`, `auth.mismatch`, `auth.token`, `auth.fail`,
`tool.start`, `tool.ok`, `tool.fail`, `resolve`, `page`, `poll`, `config`, `crash`, and the HTTP
pair (`graph.http` and `graph.http.fail` in Teams, `http` and `http.fail` in Azure DevOps).

User-authored text is not logged unless `…_LOG_CONTENT=true`, only its length. `…_LOG_LEVEL=Debug`
adds successful HTTP calls, paging and name resolution. `Trace` adds the MCP SDK's JSON-RPC
traffic.

The Azure DevOps server's `ado_auth_status` answers the first question without leaving the client:
which credential it is using, when the token expires, who Azure DevOps says it is, and whether
`AZURE_DEVOPS_PAT`, if set in the environment, is still valid.

Triage order: run `-- selftest`, which separates auth problems from tool problems. Reproduce the
failing call with `-- call <tool> key=value…`, which drives the real server path with errors
visible on the console. Then read the `startup` lines at the top of the log, and find the failing
HTTP line, which carries the service's own error body.

## Cross-platform notes

Both servers run on Windows, Linux and macOS. `%LOCALAPPDATA%` here means .NET's
local-application-data folder, wherever the platform puts it: `$XDG_DATA_HOME` (default
`~/.local/share`) on Linux and `~/Library/Application Support` on macOS.

The MSAL token cache is encrypted with whatever the OS provides: DPAPI on Windows, the Keychain on
macOS, libsecret on Linux. On headless Linux with no keyring, `auth` fails with a cache-persistence
error. The servers do not opt into the unencrypted-file fallback, so provide a keyring rather than
working around it. Sign-in works fine over SSH: the device-code flow only needs a console here and a
browser somewhere.

## Installing as .NET tools

Both servers are on nuget.org, installed in [step 1 of the quick start](#quick-start):

| Package | Tool command | Server |
| --- | --- | --- |
| [`JasonBright.Mcp.Teams`](https://www.nuget.org/packages/JasonBright.Mcp.Teams) | `teams-mcp` | Teams, through Microsoft Graph |
| [`JasonBright.Mcp.AzureDevOps`](https://www.nuget.org/packages/JasonBright.Mcp.AzureDevOps) | `ado-mcp` | Azure DevOps |

All the verbs work as they do from source. `dotnet tool update --global <id>` moves an installation
to the latest release, and both packages ship one shared version, so they move together. To run from
a checkout instead, use `dotnet run --project src/TeamsMcp -- <verb>`, as the later sections show.

The ids are owner-prefixed because `AzureDevOpsMcp`, `AdoMcp` and `AdoMcpServer` are all taken on
nuget.org by unrelated packages. nuget.org also rejects a new id differing from an existing one
only by case or separators, which ruled out `Ado.Mcp` and friends. The ids are independent of the
assembly names and the tool commands, so none of those move with them.

### Installing a local build

To run a change that has not been released, pack it and install from `artifacts/`:

```powershell
dotnet tool restore                                     # once per clone, puts nbgv on `dotnet nbgv`
dotnet pack
$v = dotnet nbgv get-version -v NuGetPackageVersion     # e.g. 0.1.3, or 0.1.3-g1a2b3c4d5e off main
dotnet tool install --global --add-source ./artifacts --version $v JasonBright.Mcp.Teams
dotnet tool install --global --add-source ./artifacts --version $v JasonBright.Mcp.AzureDevOps
```

The version pin is what makes that work. `--add-source` adds `artifacts/` to your feeds instead of
replacing them, so NuGet still resolves the highest version across all of them, which is the release
on nuget.org rather than the build you just made, and reports success either way.

A rebuild at the same version is not picked up by `dotnet tool update` either, which sees the
version as already satisfied. Uninstall and reinstall instead. `scripts/rebuild.ps1` does all of
this for both servers, against a generated config with every source cleared but `artifacts/`, and
verifies the swap actually happened.

### How the packages are published

Dispatching `.github/workflows/release.yml` from `main` is the whole of it. It packs, tags, creates
the GitHub Release, and pushes both `.nupkg` files to nuget.org.

There is no long-lived API key. The push uses
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the job
mints a GitHub OIDC token (`id-token: write` in its `permissions:`), `NuGet/login@v1` exchanges it
with nuget.org for a key that lives one hour, and the push uses that key. A policy registered on
nuget.org authorizes the exchange, naming the repository owner, the repository, and the workflow
**file name**, which is `release.yml` without the `.github/workflows/` path.

- **Renaming or moving that file breaks publishing** until the policy is edited to match. The policy
  matches the file name, not the job or the step inside it.
- **A policy covers every package its owner owns.** A third server publishes with nothing issued and
  nothing changed here. The `JasonBright.` prefix is reserved on nuget.org, so nobody else can claim
  an id under it either.

One repository secret exists, `NUGET_USER`, the nuget.org profile name (not an email) that
`NuGet/login@v1` takes. It is an identifier, not a credential; publishing is authorized by the OIDC
token and the policy behind it.

A new policy can start out provisional for seven days, which nuget.org documents as the usual case
for a private repository. Until a publish arrives carrying GitHub's immutable repository and owner
ids, nuget.org has only the strings typed into the policy, and binding to those ids stops someone
deleting a repository, recreating it under the same name, and inheriting the right to publish. The
policy works normally in that window, goes inactive if nothing is published within it (restart it
from the Trusted Publishing page), and becomes permanent on the first successful push.

The metadata behind the packages, in case a new one is added:

- **License** is MIT, set by `PackageLicenseExpression` in `Directory.Build.props`, with `LICENSE`
  at the root.
- **Package ids and tool command names** are per-csproj, since they are what make each package a
  distinct tool. Everything shared lives in `Directory.Build.props`: authors, product, tags,
  license, output path.
- **`RepositoryUrl` and `PackageProjectUrl` point at the GitHub remote**, which is public, so both
  resolve for a consumer and SourceLink has something to point at.

A published version can never be re-pushed or deleted, only unlisted, which is why the release
workflow's refusals (see [Continuous integration](#continuous-integration)) all happen before it
builds anything.

## Registering a server in a repository

```powershell
ado-mcp install            # in the repository the server should be available in
teams-mcp install
```

Installing walks up to the nearest `.git`, works out which MCP client that repository uses, and
merges the server into that client's config:

| Client | Config written | Servers property | Reference syntax | Detected from |
| --- | --- | --- | --- | --- |
| `claude` | `.mcp.json` | `mcpServers` | `${VAR}` | `.mcp.json`, `.claude/`, `CLAUDE.md` |
| `vscode` | `.vscode/mcp.json` | `servers` | `${env:VAR}` | `.vscode/mcp.json`, `.github/copilot-instructions.md` |
| `cursor` | `.cursor/mcp.json` | `mcpServers` | `${VAR}` | `.cursor/mcp.json`, `.cursorrules` |

When several clients are detected, the first wins and the others are named in the output.
`--client <name>` picks one. A repository showing signs of nothing gets the Claude Code shape.

Identity is referenced (`${ADO_MCP_TENANT_ID}`), addresses are literal, and mutation gates are never
written. Nothing already in the file is lost: other servers and top-level properties are preserved,
and an entry that already differs is a refusal printing both versions until you pass `--force`.
Re-running with the same environment does nothing. The command written follows how the install was
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
missing from the environment and whether sign-in has happened. Run `install`, then `auth`, then
`selftest`.

## Versioning

The version comes from
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning). `version.json` carries
`major.minor`, nbgv derives the patch from git height (commits since that line last changed), and no
csproj sets a version.

```powershell
dotnet tool restore              # once per clone
dotnet nbgv get-version          # what this checkout would ship as
```

An ordinary commit needs no version edit; bump `version.json` only for a new major or minor. Only
`main` produces a clean version like `0.1.4`. Everywhere else builds a `-g<commit>` prerelease, so a
branch build cannot be mistaken for a release. Computing a version needs full git history, which is
why both workflows check out with `fetch-depth: 0`.

## Continuous integration

Two GitHub Actions workflows, both on `windows-latest`, because the servers are Windows-first: the
DPAPI token cache, and `install`'s client detection. No build or test reaches Graph or Azure DevOps,
so neither workflow carries a credential. The one repository secret, `NUGET_USER`, is a nuget.org
profile name, and the key that publishes is minted per run by trusted publishing.

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `.github/workflows/pr.yml` | pull request against `main`, push to `main` | Builds the solution in Release with `-warnaserror`, runs the tests, and writes the computed version and test counts into the job summary |
| `.github/workflows/release.yml` | manual dispatch from `main` | Cuts a release, see below |

To cut a release, run the release workflow from `main`, bumping `version.json` first only for a new
major or minor. It refuses if the ref is not `main`, if nbgv calls the build a prerelease, or if the
tag already exists. Then it builds, tests, packs, tags with `nbgv tag` (`v0.1.4`), pushes the tag,
publishes a GitHub Release with both `.nupkg` files attached, and pushes both packages to nuget.org
through trusted publishing, with no API key stored anywhere. See
[How the packages are published](#how-the-packages-are-published).

## Working on this repo

```
src/TeamsMcp/                 the Teams server
src/AzureDevOpsMcp/           the Azure DevOps server
tests/TeamsMcp.Tests/         xUnit tests for the pure logic, run with `dotnet test`
tests/AzureDevOpsMcp.Tests/
plugin/                       the Claude Code plugin: both servers plus four skills
scripts/                      rebuild.ps1 (dev inner loop)
McpServers.slnx               all four projects
```

The servers are independent processes and share no code. The conventions are duplicated on purpose:
output shaped for a model's context window, `req=N` log correlation, gated mutations, the log
format. They will be factored into a library when a third server forces it.

Design documentation is in [`docs/`](docs/): architecture, the authentication split, the tool
conventions, the logging design, a document per server, and the decision records.

`.mcp.json` registers a C# language server as an MCP server, so an agent editing this code can
resolve symbols instead of grepping. Its tools are not checked in, so install them once:

```powershell
dotnet tool install --global csharp-ls
dotnet tool install --global CSharpLspMcp
```

Call `csharp_set_workspace` with the solution or project path before the other tools work.
