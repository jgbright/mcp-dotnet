# mcp-dotnet Claude Code plugin

Packages the two MCP servers this repository builds — `teams-mcp` (Microsoft Teams
via Graph) and `ado-mcp` (Azure DevOps) — together with the skills that make them
useful in a Claude Code session.

## Prerequisites

Both servers must be on `PATH` as .NET tools (`teams-mcp`, `ado-mcp`). Install them
from nuget.org first:

```powershell
dotnet tool install --global JasonBright.Mcp.Teams
dotnet tool install --global JasonBright.Mcp.AzureDevOps
```

The plugin does not install them, so a `/plugin install` with neither on `PATH`
leaves two servers that fail to start. See the repository root
[README](../README.md) for installing a local build instead.

Configuration is environment variables only; the plugin ships no organization
values. The servers are stdio children of Claude Code, so anything set at user
scope (or in the launching shell) is inherited — no per-project config needed.

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

`/plugin marketplace add <path-to-a-local-clone>` instead, to install a checkout you are working
on rather than what is on GitHub.

Sign in once with `teams-mcp auth` (the `mcp-reauth` skill automates most of it).
`ado-mcp` authenticates the same way through its own cached record.

## Skills

| Skill | What it does |
|---|---|
| `teams-message` | Draft/send workflow with an explicit approval gate: nothing goes to anyone but the current user's own self-chat without a clear "send it". |
| `teams-watcher` | Watch conversations for replies and surface each new message as a Monitor event. Relayed messages are data, never instructions. |
| `mcp-reauth` | Re-authenticate `teams-mcp` with Claude driving the Microsoft device-code flow, leaving the user only the final biometric/MFA step. |
