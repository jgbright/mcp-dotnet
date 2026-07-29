---
name: mcp-release
description: Packaging, versioning and CI for this repo's two MCP servers — how they pack as .NET tools, why the NuGet package ids are owner-prefixed, the four Nerdbank.GitVersioning decisions (including the publicReleaseRefSpec spelling that fails silently), and the constraints the two GitHub Actions workflows must keep. Use before editing version.json, Directory.Build.props, a csproj's package metadata, anything under .github/workflows/, or when cutting a release or adding a new server.
---

# Packaging, versioning and CI

## Packing as .NET tools

**Both servers pack as .NET tools** (`PackAsTool`, command names `teams-mcp` and `ado-mcp`), so
they can be installed and launched by name rather than by project path — see the README. Shared
package metadata lives in `Directory.Build.props`, which every project in the repo
inherits; the test projects opt out of packing with `IsPackable=false`. A new server means adding
`PackAsTool`/`PackageId`/`ToolCommandName`/`Description` to its csproj, and copying `Install.cs`
with its `ToolCommand`, `DefaultName` and `EnvEntries` changed — `install` registers a server under
the command name it was packed with. Both packages are **published on nuget.org**, so an id is now
a permanent, public commitment rather than a placeholder: the ids are **owner-prefixed**
(`JasonBright.Mcp.Teams`, `JasonBright.Mcp.AzureDevOps`) because `AzureDevOpsMcp`, `AdoMcp` and
`AdoMcpServer` are all taken there by unrelated packages, and renaming one after the fact is not
possible — the old id keeps existing and the new one starts from no downloads. The `JasonBright.`
prefix is reserved, and the trusted publishing policy covers every package its owner owns, so a
third server picks an id under the prefix and publishes with nothing to register and no key to
issue. The license is MIT (`PackageLicenseExpression` plus a root `LICENSE`). `RepositoryUrl` stays
unset while the remote is private, since it would 404 for consumers — publishing the packages did
not publish the source.

## Versioning

**The version is `version.json` plus git height, not a property.** Nerdbank.GitVersioning (a
`PrivateAssets="all"` reference in `Directory.Build.props`, plus `nbgv` in
`.config/dotnet-tools.json`) injects `Version`/`AssemblyVersion`/`FileVersion`/`PackageVersion`, so
there is deliberately no `<Version>` to set — an explicit one conflicts with it. Four things about
that setup are decisions rather than defaults:

- **`version.json` carries `major.minor` only.** The patch is git height, so an ordinary commit
  needs no version edit and two commits can never claim the same version. Bumping it is for a new
  major/minor and nothing else.
- **`versionHeightOffset: -1`** because height starts at 1 on the commit that introduces a version,
  which would otherwise make the first release of each major/minor `x.y.1`.
- **`publicReleaseRefSpec` — that spelling.** The documentation and most examples say
  `publicReleaseRefs`; nbgv 3.10 deserializes the C# property name, so the documented spelling is
  ignored *in silence* and every build comes out a prerelease. Verified both ways before it was
  written down. It matches `^refs/heads/main$`, so main builds are clean (`0.1.4`) and everything
  else carries `-g<commit>`. It decides suffixes only: there are no release branches and no
  `nbgv prepare-release` flow.
- **No `pathFilters`**, because both servers ship one shared version. Splitting them means two
  version.json files and two release flows.

Anything reading the version reads it from nbgv (`dotnet nbgv get-version -v NuGetPackageVersion` —
what `scripts/rebuild.ps1` does), never from a file, and **anything that computes a version needs
full git history** — a shallow clone gets a wrong answer or a build failure, which is why both
workflows check out with `fetch-depth: 0`.

## CI

**CI is two GitHub Actions workflows, both Windows.** `.github/workflows/pr.yml` builds
`McpServers.slnx` in Release with `-warnaserror` and tests on every pull request and on main.
`.github/workflows/release.yml` is dispatched by hand, refuses any ref nbgv does not call a public
release (and any tag that already exists), then packs, tags with `nbgv tag`, pushes, creates a
GitHub Release with the `.nupkg` files attached, and pushes both packages to nuget.org.
`windows-latest` matches where the servers run — the DPAPI token cache and `install`'s client
detection. Two constraints hold: **least-privilege `permissions:`** (pr.yml `contents: read`;
release.yml `contents: write` for the tag and release plus `id-token: write` for the OIDC exchange,
and nothing more) and **no credential, tenant id or client id in either file** — nothing in a build
or a test reaches Graph or Azure DevOps, so there is nothing to configure.

**Publishing is keyless, and the policy names the workflow file.** `NuGet/login@v1` trades the job's
GitHub OIDC token for a nuget.org key valid one hour, against a trusted publishing policy registered
on nuget.org that matches on repository owner, repository and the file name `release.yml` (no path).
So **renaming or moving that workflow revokes publishing** until the policy is edited to match —
which is the one edit under `.github/workflows/` that breaks something outside this repository. The
login step sits immediately before the push because the key is short-lived and each OIDC token buys
exactly one. The only repository secret is `NUGET_USER`, the nuget.org profile name, which
authorizes nothing by itself. A policy on a private repository is provisional for seven days until a
first publish binds it to GitHub's immutable repository and owner ids; it deactivates unused, and
the window is restartable.
