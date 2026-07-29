# Contributing

## Build, test, pack

```powershell
dotnet tool restore   # once per clone, the version comes from nbgv rather than a csproj
dotnet build
dotnet test
dotnet pack           # both servers, as .NET tool packages, into artifacts/
```

The version is `version.json` plus git height, so a normal change needs no version edit. `main`
builds clean and everything else is a `-g<commit>` prerelease. `dotnet nbgv get-version` says
what a checkout would ship as. Pull requests are built by `.github/workflows/pr.yml` (Release,
warnings as errors, tests). Releases are cut by dispatching `.github/workflows/release.yml` from
`main`, which tags, publishes a GitHub Release, and pushes both packages to nuget.org
([`JasonBright.Mcp.Teams`](https://www.nuget.org/packages/JasonBright.Mcp.Teams),
[`JasonBright.Mcp.AzureDevOps`](https://www.nuget.org/packages/JasonBright.Mcp.AzureDevOps)). That
push is irreversible, and nuget.org authorizes it against a trusted publishing policy naming
`release.yml` by file name — so renaming that workflow breaks publishing. The README's *Versioning*,
*Continuous integration* and *How the packages are published* sections have the detail.

`dotnet test` covers everything that does not need the remote service: body conversion and
truncation, DTO mapping, name resolution, WIQL construction, exception mapping, the log format,
and all of `install`. Anything that talks to Graph or Azure DevOps is verified by hand:
`-- selftest` exercises the real silent-auth path in console mode, and a tool change is proven by
registering the server in an MCP client and calling it. `scripts/rebuild.ps1` is the inner loop
for that. It builds, tests, packs, and swaps the installed .NET tools in place, which plain
`dotnet tool update` cannot do because the dev version never moves.

Tests reach the helpers they exercise as `internal` via `InternalsVisibleTo`. Prefer widening
something to `internal` over reshaping code to make it testable.

## Ground rules

A change that breaks one of these will be asked to change.

- **stdout belongs to the MCP transport.** In anything that runs in server mode: no
  `Console.WriteLine`, no `AddConsole()`, no stdout logger. It corrupts the JSON-RPC stream. Logs
  go to the file sink and stderr. The `auth`, `selftest`, `install` and `config` verbs return
  before the host is built and may print freely.
- **The two servers share no code, on purpose.** The conventions (log format, `Run` wrapper, DTO
  style, `Install`) are duplicated. Do not extract a shared library on the strength of the
  similarity. The plan is to factor one out when a third server or a real divergence forces it.
  Until then, changing a shared convention means changing it in both servers.
- **Mutations are opt-in.** Anything that changes what other people can see goes behind an
  environment gate: `TEAMS_MCP_ALLOW_SEND` for Teams, `ADO_MCP_ALLOW_WRITE` for Azure DevOps. A
  new mutating tool calls the existing gate helper instead of inventing policy.
- **User-authored content is not logged by default.** New tool arguments carrying message bodies,
  descriptions or comments are logged as `{field}.len=N` unless `…_LOG_CONTENT=true`. Ids, names
  and counts are logged in full. Use the existing `ContentArg` / `Arg` helpers.
- **Organization-specific knowledge is configuration, never code.** No tag name, TFVC path,
  release definition or per-organization heuristic belongs in this repo. Extend the mechanism and
  put the facts in the external data files.
- **Output is shaped for a model's context window.** Nulls are omitted by the serializer, fields
  that merely restate the common case are set to null, filtered items are counted in `skipped`
  rather than silently dropped, and tool parameter names are `snake_case`.

`CLAUDE.md` in the repo root carries the full conventions in more detail. It is written as
guidance for coding agents, but it is the same rulebook and worth reading before a non-trivial
change.

[`docs/`](docs/) has the design behind those rules: [architecture](docs/architecture.md) for the
shape of a server and how a tool call flows through it, [tool-contract.md](docs/tool-contract.md)
for the checklist a new tool has to satisfy, and a document per server for the service-specific
traps. Start there before a change that adds a tool, touches authentication, or alters what a
result looks like.
