# Documentation

Design docs for the two MCP servers in this repository.

## Contents

| Document | Covers |
| --- | --- |
| [architecture.md](architecture.md) | The two processes, how a tool call flows through one, the file map, state and concurrency, extending a server |
| [authentication.md](authentication.md) | Why sign-in is split in two, what is persisted where, the invariants that keep the cache warm, Teams' scope consent |
| [tool-contract.md](tool-contract.md) | The conventions every tool follows: the `Run` wrapper, output shaping, `skipped`, name resolution, paging, annotations, structured content, long waits as tasks |
| [observability.md](observability.md) | The logging stack, the line format, the event vocabulary, content redaction, triage order |
| [teams-server.md](teams-server.md) | Graph specifics: the message pager, the watermark/cursor design behind the waiters, the four search tools over one untyped index |
| [azure-devops-server.md](azure-devops-server.md) | REST specifics: the hand-rolled client, the four service hosts, paging strategies, work item writes over JSON Patch, the deployment map |
| [distribution.md](distribution.md) | How the servers reach a machine: .NET tools, the Claude Code plugin, the `install` verb, versioning, CI |

## Elsewhere

| For | Read |
| --- | --- |
| Configuring, signing in, calling a tool | [`README.md`](../README.md) |
| Building, testing and landing a change | [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
| The rules a change has to keep | [`CLAUDE.md`](../CLAUDE.md), written for coding agents but the same rulebook |
| Packaging, versioning, releasing | the [`mcp-release`](../.claude/skills/mcp-release/SKILL.md) skill |
| Diagnosing a failing server from its log | the [`mcp-log-diagnostics`](../.claude/skills/mcp-log-diagnostics/SKILL.md) skill |

A document that names a constant or a limit names the file that holds it, so drift is findable.
