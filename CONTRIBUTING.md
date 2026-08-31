# Contributing

## Build, test, pack

```powershell
dotnet tool restore         # once per clone, the version comes from nbgv rather than a csproj
dotnet build -warnaserror   # CI builds this way; a warning here is a red build there
dotnet test
dotnet pack                 # both servers, as .NET tool packages, into artifacts/
```

The version is `version.json` plus git height, so a normal change needs no version edit. `main`
builds clean and everything else is a `-g<commit>` prerelease. `dotnet nbgv get-version` says what
a checkout would ship as.

`.github/workflows/pr.yml` builds pull requests (Release, warnings as errors, tests). A release is
cut by dispatching `.github/workflows/release.yml` from `main`: it tags, publishes a GitHub Release
and pushes both packages to nuget.org
([`JasonBright.Mcp.Teams`](https://www.nuget.org/packages/JasonBright.Mcp.Teams),
[`JasonBright.Mcp.AzureDevOps`](https://www.nuget.org/packages/JasonBright.Mcp.AzureDevOps)). That
push is irreversible, and nuget.org authorizes it against a trusted publishing policy naming
`release.yml` by file name, so renaming that workflow breaks publishing. The README's *Versioning*,
*Continuous integration* and *How the packages are published* sections have the detail.

`dotnet test` covers everything that does not need the remote service: body conversion and
truncation, DTO mapping, name resolution, WIQL construction, exception mapping, the log format, and
all of `install`. Anything that talks to Graph or Azure DevOps is verified by hand. `-- selftest`
runs the real silent-auth path in console mode; `-- call <tool> key=value…` drives one tool
through the real server path with the result on stdout. Registering the server in an MCP client
stays the check that the client sees what it should. `scripts/rebuild.ps1` is the inner loop for
that: it builds, tests, packs, and swaps the installed .NET tools in place, which
`dotnet tool update` cannot do because the dev version never moves.

Tests reach helpers as `internal` via `InternalsVisibleTo`. Prefer widening something to
`internal` over reshaping code to make it testable.

## Ground rules

- **stdout belongs to the MCP transport.** In anything that runs in server mode: no
  `Console.WriteLine`, no `AddConsole()`, no stdout logger. It corrupts the JSON-RPC stream. Logs
  go to the file sink and stderr. The `auth`, `selftest`, `install` and `config` verbs return
  before the host is built and may print freely; `call` builds the host but moves its transport
  onto in-memory pipes.
- **The two servers share no code, on purpose.** The conventions (log format, `Run` wrapper, DTO
  style, `Install`) are duplicated. Do not extract a shared library on the strength of that
  similarity; the plan is to factor one out when a third server or a real divergence forces it.
  Until then, changing a shared convention means changing it in both servers.
- **Anything that changes what other people can see goes behind an environment gate**:
  `TEAMS_MCP_ALLOW_SEND` for Teams, `ADO_MCP_ALLOW_WRITE` for Azure DevOps. A new mutating tool
  calls the existing gate helper instead of inventing policy.
- **User-authored content is not logged by default.** Tool arguments carrying message bodies,
  descriptions or comments log as `{field}.len=N` unless `…_LOG_CONTENT=true`. Ids, names and
  counts log in full. Use the existing `ContentArg` / `Arg` helpers.
- **Organization-specific knowledge is configuration, never code.** No tag name, TFVC path,
  release definition or per-organization heuristic belongs in this repo. Extend the mechanism and
  put the facts in the external data files.
- **Output is shaped for a model's context window**: the serializer omits nulls, fields that merely
  restate the common case are set to null, filtered items are counted in `skipped` rather than
  silently dropped, and tool parameter names are `snake_case`.

`CLAUDE.md` in the repo root has the same rules in more detail. It is written for coding agents;
read it before a non-trivial change.

[`docs/`](docs/) has the design behind the rules: [architecture.md](docs/architecture.md) for the
shape of a server and how a tool call flows through it, [tool-contract.md](docs/tool-contract.md)
for the checklist a new tool has to satisfy, and one document per server for the service-specific
traps. Read those before adding a tool, touching authentication, or changing what a result looks
like.
