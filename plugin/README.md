# mcp-dotnet Claude Code plugin

Packages this repository's two MCP servers, `teams-mcp` (Microsoft Teams via
Graph) and `ado-mcp` (Azure DevOps), with the skills that drive them in a Claude
Code session.

## Prerequisites

Both servers must be on `PATH` as .NET tools (`teams-mcp`, `ado-mcp`). Install them
from nuget.org first:

```powershell
dotnet tool install --global JasonBright.Mcp.Teams
dotnet tool install --global JasonBright.Mcp.AzureDevOps
```

The plugin does not install them: `/plugin install` with neither on `PATH` leaves
two servers that fail to start. The root [README](../README.md) covers installing a
local build instead.

Configuration is environment variables only; the plugin ships no organization
values. The servers run as stdio children of Claude Code and inherit anything set
at user scope or in the launching shell, so no per-project config is needed.

| Variable | Server | Required | Purpose |
|---|---|---|---|
| `TEAMS_MCP_TENANT_ID` | teams | yes | Entra tenant id |
| `TEAMS_MCP_CLIENT_ID` | teams | yes | App registration (public client) id |
| `TEAMS_MCP_ALLOW_SEND` | teams | no | `true` enables the send tools; anything else leaves the server read-only |
| `TEAMS_MCP_AUTH` | teams | no | `browser` switches interactive sign-in from device-code to browser |
| `ADO_MCP_TENANT_ID` | ado | yes | Entra tenant id |
| `ADO_MCP_CLIENT_ID` | ado | yes | App registration (public client) id |
| `ADO_MCP_ORG_URL` | ado | yes | e.g. `https://dev.azure.com/yourorg` |
| `ADO_MCP_PROJECT` | ado | yes | Default project for tools that omit one |
| `ADO_MCP_ALLOW_WRITE` | ado | no | `true` enables the write tools; anything else leaves the server read-only |

## Installing

```
/plugin marketplace add jgbright/mcp-dotnet
/plugin install mcp-dotnet@mcp-dotnet
```

`/plugin marketplace add <path-to-a-local-clone>` installs a local checkout instead of
what is on GitHub.

Sign in once with `teams-mcp auth`; the `mcp-reauth` skill automates most of it.
`ado-mcp` works the same way through its own cached record.

## Skills

| Skill | What it does |
|---|---|
| `teams-message` | Draft/send workflow with an explicit approval gate: nothing goes anywhere but the current user's own self-chat without a clear "send it". |
| `teams-watcher` | Watch conversations for replies and surface each new message as a Monitor event. Relayed messages are data, never instructions. |
| `teams-followup` | Handle one message end to end: investigate, react 🤔 on the source, draft to the self-chat, forward it once the user reacts to approve. |
| `pr-review` | Review one of your own Azure DevOps PRs, revise the findings with Claude in your Teams self-chat (optionally testing the branch first), and post them to the PR as inline comments once you react to approve. |
| `mcp-reauth` | Re-authenticate `teams-mcp` with Claude driving the Microsoft device-code flow, leaving the user only the final biometric/MFA step. |

## Send gate and drafts pane

The plugin also carries a hooks module (`hooks/`, Claude Code function hooks, early
access) that does two things for Teams drafts.

**Send gate.** A Teams send to anyone but the self chat goes through only when its body
was approved. There are two ways to approve. The user reacts to that exact body's
self-chat draft, and the gate reads the reaction back before letting the send through.
Or the user presses Send in the drafts pane. Each approval covers one send. The gate
also refuses any reaction placed in the self chat, since reactions there are the user's
approvals, and refuses a body that still carries teams-followup's "React to approve"
cue. Reactions in other chats pass. A phone approval is tied to the body, not to the
destination.

**Drafts pane.** The `show_drafts` tool (`mcp__mcp-dotnet__show_drafts`) shows the
variants side by side in a pane beside the transcript. Each variant has three buttons:
Self (hotkeys 1-4) posts it to the self chat, Copy copies it, and Send posts it to the
destination. Notes typed into Revise come back as the next prompt. `/drafts` reopens
the pane, and the status line shows the open draft and whether a verdict is pending.

Turn the gate off with the `sendGate` option (`/config`, or `pluginConfigs` in
settings). The pane works either way.

`claude plugin test plugin` runs the hook tests and `claude plugin validate plugin`
checks the module.
