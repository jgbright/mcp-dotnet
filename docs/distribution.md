# Distribution

How the servers get onto a machine and into an MCP client, how they are versioned, and what CI does.

The [`mcp-release`](../.claude/skills/mcp-release/SKILL.md) skill is the authority for packaging,
versioning and CI mechanics. Read it before touching `version.json`, `Directory.Build.props`, a
csproj's package metadata, or anything under `.github/workflows/`.

## The layers

```
NuGet package  →  .NET global tool on PATH  →  registered with an MCP client
```

The layers are independent. The binary always arrives as a .NET tool. How a client learns to launch
it has two answers today.

### Packaging as .NET tools

Both csprojs set `PackAsTool` with a `ToolCommandName`, so `dotnet pack` produces packages that
`dotnet tool install` puts on PATH as `teams-mcp` and `ado-mcp`. Both are published on nuget.org.

| Package id | Tool command | Assembly |
| --- | --- | --- |
| `JasonBright.Mcp.Teams` | `teams-mcp` | `TeamsMcp.dll` |
| `JasonBright.Mcp.AzureDevOps` | `ado-mcp` | `AzureDevOpsMcp.dll` |

The ids are owner-prefixed because `AzureDevOpsMcp`, `AdoMcp` and `AdoMcpServer` are all taken on
nuget.org by unrelated packages, and nuget.org rejects a new id differing from an existing one only
by case or separators, which rules out `Ado.Mcp` and the rest of that family too. Package ids are
independent of assembly names and tool commands, so neither moves with them. The `JasonBright.`
prefix is reserved on nuget.org, so a third server's id is a packaging decision and not a name to
race someone for.

`RepositoryUrl` and `PackageProjectUrl` name the public GitHub remote, so a consumer following
either gets somewhere and SourceLink has a repository to resolve against.

`Directory.Build.props` holds authors, product, tags, output path, and the MIT
`PackageLicenseExpression`. Deliberately not there: `Version` (nbgv owns it), and
`PackageId`/`ToolCommandName`, which are per project because they make each package a distinct tool.

### Registering with a client

The **plugin** (`plugin/`, published through `.claude-plugin/marketplace.json`) is the current path.
It bundles a `.mcp.json` declaring both servers by PATH command with no environment at all, plus the
`teams-message`, `teams-followup`, `teams-watcher` and `mcp-reauth` skills. Install with
`/plugin marketplace add jgbright/mcp-dotnet` (or a local clone's path) then
`/plugin install mcp-dotnet@mcp-dotnet`; it updates as one unit.

The servers are stdio children of the client, so they inherit their configuration from user-scope
environment variables. The plugin ships no organization values.

Skills and servers ship together because the watcher skill is a thin client of the waiter contract
(`chats`, `cursor`/`nextCursor`). Versioning them separately produced a silent wire-format break
once already.

The **`install` verb** is the older path and still present in both servers. It finds the repository
by walking up to a `.git`, decides which MCP client the repository uses from marker files, and
merges one entry into that client's config:

| Client | Config written | Servers property | Reference syntax | Detected from |
| --- | --- | --- | --- | --- |
| `claude` | `.mcp.json` | `mcpServers` | `${VAR}` | `.mcp.json`, `.claude/`, `CLAUDE.md` |
| `vscode` | `.vscode/mcp.json` | `servers` | `${env:VAR}` | `.vscode/mcp.json`, `.github/copilot-instructions.md` |
| `cursor` | `.cursor/mcp.json` | `mcpServers` | `${VAR}` | `.cursor/mcp.json`, `.cursorrules` |

`install` edits somebody else's repository, so it is a merge and never a write-over. These rules
should survive any change to it:

- An entry that already differs is a refusal, printing both versions, until `--force`. Re-running
  with the same environment is a no-op. Other servers and other top-level properties are preserved.
- Identity is referenced (`${ADO_MCP_TENANT_ID}`), addresses are literal (organization URL, default
  project), and mutation gates are never written. The config usually ends up committed, and an app
  registration and a send/write gate belong to whoever runs the server.
- Clients are data, not code paths. A client is a config path, a servers property, an env-reference
  syntax and its marker files (`Install.Clients`). Supporting another one means adding a row;
  anything that cannot be expressed as a row is a reason to reconsider rather than to branch.

ADR 0001 records the intent to retire `install` when the plugin lands. The plugin has landed;
`install`, `Install.cs` and its test suites have not been removed. That is the outstanding half of
that decision, not an oversight to be rediscovered.

### The dev inner loop

`scripts/rebuild.ps1` builds, tests, packs, and swaps the installed tools in place. It is a script
rather than a remembered command line because these failure modes all look like success:

- `dotnet tool update` cannot pick up a rebuild. The version comes from git height, so it moves on a
  commit but not on an edit; update sees the version already satisfied and exits 0 without replacing
  anything. Uninstall-then-install is the only sequence that swaps files at an unchanged version.
- A running server holds its own DLL open, so the uninstall fails with access denied and the install
  afterwards reports success against untouched files. The script stops instances immediately before
  each uninstall (a supervisor may relaunch one during the build) and retries once.
- `--add-source` adds a source rather than restricting to one, so an unpinned install resolves the
  highest version across every feed, which is not the local build. Installs run against a generated
  config with every source cleared but `artifacts/`, and the version pinned.

It then verifies the install: the assembly timestamp must have changed and the tool command must be
the expected one. Either mismatch is a hard failure.

An MCP client launches its servers at startup, so a session open across a rebuild keeps the old
binary until it restarts.

## Versioning

[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning). `version.json` carries
`major.minor`, nbgv derives the patch from git height, and no csproj sets a version. An ordinary
commit needs no version edit; bump `version.json` only for a new major/minor.

The decisions recorded in `version.json`'s own comments:

- `versionHeightOffset: -1`, so a new major/minor ships as `x.y.0` rather than `x.y.1`.
- `publicReleaseRefSpec` — *not* `publicReleaseRefs`, which is what most examples show and what
  the documentation calls the feature. nbgv 3.10 deserializes the C# property name; an entry
  under the other spelling is silently ignored and every build looks like a prerelease.
- No `pathFilters`: both servers ship one shared version, so a commit touching either advances both.
  Splitting them means two `version.json` files and two release flows.
- Only `main` produces a clean version. Everywhere else builds a `-g<commit>` prerelease, so a
  branch build cannot be mistaken for a release.

nbgv needs real git history, which is why both workflows check out with `fetch-depth: 0`.

## Continuous integration

Two workflows, both on `windows-latest`, since the servers are Windows-first: DPAPI token cache,
`install`'s client detection. Neither carries a credential. No build or test reaches Graph or Azure
DevOps, and the publish key is minted per run rather than stored.

| Workflow | Trigger | Does |
| --- | --- | --- |
| `pr.yml` | PR against `main`, push to `main` | Builds Release with `-warnaserror`, runs the tests, writes the computed version and test counts into the job summary |
| `release.yml` | Manual dispatch from `main` | Refuses unless the ref is `main`, nbgv calls it a public release, and the tag does not exist; then builds, tests, packs, tags with `nbgv tag`, pushes the tag, publishes a GitHub Release with both `.nupkg` files attached, and pushes both to nuget.org |

The nuget.org push is the only irreversible step, so it runs last: a published version can never be
re-pushed or deleted, only unlisted. Everything that can fail cheaply fails first, so a failure at
the push leaves a tagged release short a feed entry rather than a feed entry with nothing behind it.
`--skip-duplicate` makes re-dispatching after a half-finished push a no-op on whichever package
already landed.

### Trusted publishing

The push authenticates with [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
rather than a stored API key. The job mints a GitHub OIDC token, `NuGet/login@v1` exchanges it with
nuget.org for a key valid one hour, and `dotnet nuget push` uses that. The exchange is authorized by
a policy registered on nuget.org against the repository owner, the repository, and the workflow
**file name**: `release.yml`, without its path. Before changing anything near it:

- The policy names the file, so renaming or moving `release.yml` revokes the right to publish until
  the policy is edited to match. It fails loudly, but at the worst moment: the login step is
  second-to-last, so the tag and the GitHub Release already exist by then. The policy does not name
  the job, the step, or the branch; the branch is the workflow's own `main` gate.
- A policy covers every package its owner owns, so a third server needs no new registration. Same
  reason the reserved `JasonBright.` prefix matters: a new id is a packaging decision, not an access
  one.
- A new policy can be provisional for seven days, which nuget.org documents as the usual case for a
  private repository. It holds only the strings typed into it until a publish arrives carrying
  GitHub's immutable repository and owner ids; binding to those defeats deleting a repository and
  recreating it under the same name to inherit its publishing rights. It is fully usable in that
  window, deactivates if it goes unused, and the window can be restarted. The first successful push
  makes it permanent.

The one repository secret, `NUGET_USER`, is the nuget.org profile name the login action takes. It is
an identifier, not a credential, and authorizes nothing by itself.
