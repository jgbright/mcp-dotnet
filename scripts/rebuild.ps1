#Requires -Version 7
<#
.SYNOPSIS
Rebuilds the MCP servers from source and reinstalls them as global .NET tools.

.DESCRIPTION
Builds, tests, packs, then replaces the installed tools and verifies the replacement landed.
This is the inner loop for changing a server and running the change under a real MCP client.

The failure modes it works around make this a script instead of a remembered command line, and
each one looks like success:

- `dotnet tool update` cannot pick up a rebuild. The version comes from Nerdbank.GitVersioning
  (version.json plus git height), so it moves on a commit but not on an edit. Update sees the
  version already satisfied and exits 0 without replacing anything. Uninstall then install is the
  only sequence that swaps the files at an unchanged version.
- A running server holds its own DLL open, so the uninstall fails with access denied and the
  install afterwards reports success against untouched files. Instances are stopped immediately
  before each uninstall, since a supervisor may relaunch one during the build. That drops the MCP
  connection of any client currently using these servers.
- `--add-source` adds a source instead of restricting to one, so an unpinned install resolves the
  highest version across every configured feed. That used to mean a stranger's package with the
  same id; now the ids are owner-prefixed it means whatever this repo has published to nuget.org,
  which is still not the local build. Installs therefore run against a config with every source
  cleared but artifacts/, with the version pinned.

The install is verified: the assembly timestamp is compared before and after, and the tool command
is checked. Either mismatch is a hard failure.

MCP clients launch their servers at startup, so a client already running when this finishes keeps
the old binary until it restarts. Restart it.

.EXAMPLE
./rebuild.ps1                        # both servers: build, test, pack, reinstall
./rebuild.ps1 -Server Teams          # just the Teams server
./rebuild.ps1 -SkipTests             # skip the test gate on a tight edit loop

.NOTES
Run from anywhere. Paths resolve relative to the repository this script lives in.
It never starts a server, so it needs none of their environment variables and no sign-in.
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Teams', 'Ado')] [string]$Server = 'All',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Artifacts = Join-Path $RepoRoot 'artifacts'

# The install pins this version, so read it rather than assume it. The NuGet form, not the plain
# one: off main it carries a -g<commit> suffix, and the pinned install has to ask for the version
# the package was stamped with.
Push-Location $RepoRoot
try { $Version = & dotnet nbgv get-version --variable NuGetPackageVersion }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or -not $Version)
{
    throw 'Could not compute the version. Run `dotnet tool restore` in the repository root first.'
}
$Version = $Version.Trim()

# PackageId doubles as the tool-store directory name, lowercased. ToolCommand is both the process
# name to stop and the command the install puts on PATH.
$Servers = @(
    [PSCustomObject]@{
        Key         = 'Teams'
        PackageId   = 'JasonBright.Mcp.Teams'
        ToolCommand = 'teams-mcp'
        Assembly    = 'TeamsMcp.dll'
    }
    [PSCustomObject]@{
        Key         = 'Ado'
        PackageId   = 'JasonBright.Mcp.AzureDevOps'
        ToolCommand = 'ado-mcp'
        Assembly    = 'AzureDevOpsMcp.dll'
    }
)

$Targets = if ($Server -eq 'All') { $Servers } else { $Servers | Where-Object Key -eq $Server }

function Write-Step([string]$Text)
{
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# The installed assembly, or $null when the tool is not installed. Its LastWriteTime is the only
# evidence that an install replaced anything.
function Get-InstalledAssembly($Target)
{
    $store = Join-Path $env:USERPROFILE ".dotnet\tools\.store\$($Target.PackageId.ToLowerInvariant())"
    if (-not (Test-Path $store)) { return $null }
    return Get-ChildItem $store -Recurse -Filter $Target.Assembly -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

# Stops every instance of one server so its files can be replaced; returns whether it killed
# anything. Called immediately before that server's uninstall, not once up front: a supervisor (an
# MCP client that reconnects, a watcher on a timer) relaunches the server during the build, and the
# lock is back by the time the uninstall runs.
function Stop-ServerProcess($Target)
{
    $running = Get-Process -Name $Target.ToolCommand -ErrorAction SilentlyContinue
    if (-not $running) { return $false }

    foreach ($p in $running)
    {
        Write-Host "    stopping $($p.Name) pid=$($p.Id) (its client has lost the connection)" -ForegroundColor Yellow
    }
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 750   # handles are released asynchronously
    return $true
}

# dotnet writes its own diagnostics. This just stops the script on a non-zero exit, which
# $ErrorActionPreference does not do for native commands.
function Invoke-Dotnet([string[]]$DotnetArgs)
{
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs -join ' ') failed with exit code $LASTEXITCODE" }
}

# `--add-source` ADDS a source instead of restricting to one, so NuGet still resolves the highest
# version across every feed. An unpinned install then reports success while registering a package
# that is not the local build: historically a stranger's AzureDevOpsMcp, and once these servers
# publish, their own released version. So every source is cleared but artifacts/, and the install
# pins the version besides.
$NugetConfig = Join-Path ([System.IO.Path]::GetTempPath()) "mcp-rebuild-$PID.nuget.config"
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$Artifacts" />
  </packageSources>
</configuration>
"@ | Set-Content $NugetConfig -Encoding UTF8

Push-Location $RepoRoot
try
{
    # ---------------------------------------------------------------- build, test, pack

    Write-Step 'Building'
    Invoke-Dotnet @('build', '--nologo')

    if ($SkipTests)
    {
        Write-Step 'Skipping tests (-SkipTests)'
    }
    else
    {
        Write-Step 'Testing'
        Invoke-Dotnet @('test', '--nologo', '--no-build')
    }

    # No --no-build here: PackAsTool runs a publish as part of packing, and suppressing the build
    # suppresses that too, producing a package whose tool payload is stale or missing.
    Write-Step 'Packing'
    Invoke-Dotnet @('pack', '--nologo')

    # ---------------------------------------------------------------- reinstall

    # Collected explicitly, not by capturing the loop: dotnet writes to stdout, so a captured loop
    # body picks up its chatter alongside the result objects.
    $results = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($t in $Targets)
    {
        Write-Step "Reinstalling $($t.PackageId)"

        $before = Get-InstalledAssembly $t
        $stamp = if ($before) { $before.LastWriteTime } else { $null }
        Write-Host "    installed before: $(if ($stamp) { $stamp } else { '<not installed>' })"

        if ($stamp)
        {
            [void](Stop-ServerProcess $t)

            # Not Invoke-Dotnet: an access-denied here is the lock case, and the install that
            # follows must not paper over it.
            & dotnet tool uninstall --global $t.PackageId
            if ($LASTEXITCODE -ne 0)
            {
                # A supervisor can relaunch the server inside the window just used. One retry covers
                # that; a second failure is something else holding the files.
                Write-Host '    uninstall blocked, retrying once' -ForegroundColor Yellow
                [void](Stop-ServerProcess $t)
                & dotnet tool uninstall --global $t.PackageId
            }
            if ($LASTEXITCODE -ne 0)
            {
                throw "Uninstall of $($t.PackageId) failed. Something still holds its files open. " +
                      "Check for a stray $($t.ToolCommand) process or a client that keeps restarting one."
            }
        }

        Invoke-Dotnet @('tool', 'install', '--global', '--configfile', $NugetConfig,
                        '--version', $Version, $t.PackageId)

        $after = Get-InstalledAssembly $t
        if (-not $after) { throw "$($t.PackageId) reports installed but no $($t.Assembly) is in the tool store." }
        if ($stamp -and $after.LastWriteTime -eq $stamp)
        {
            throw "$($t.PackageId) still has its previous assembly ($stamp). The install reported " +
                  'success without replacing anything.'
        }

        # A package resolved from the wrong source brings its own tool command. A wrong command name
        # breaks every MCP registration pointing at it while `dotnet tool list` looks healthy.
        $listed = (& dotnet tool list --global) -match "^\s*$($t.PackageId.ToLowerInvariant())\s"
        if ($listed -and $listed -notmatch "\s$([Regex]::Escape($t.ToolCommand))\s*$")
        {
            throw "$($t.PackageId) installed under a different tool command than $($t.ToolCommand): " +
                  "'$($listed.Trim())'. That is a foreign package with the same id."
        }

        Write-Host "    installed after : $($after.LastWriteTime)" -ForegroundColor Green

        $results.Add([PSCustomObject]@{
            Server   = $t.PackageId
            Command  = $t.ToolCommand
            Assembly = $after.LastWriteTime
            OnPath   = [bool](Get-Command $t.ToolCommand -ErrorAction SilentlyContinue)
        })
    }

    Write-Step 'Done'
    $results | Format-Table -AutoSize

    if ($results | Where-Object { -not $_.OnPath })
    {
        Write-Host 'Some commands are not resolving on PATH; ~/.dotnet/tools may be missing from it.' -ForegroundColor Yellow
    }

    Write-Host 'Restart any MCP client (Claude Code sessions included): servers are launched at client startup,' -ForegroundColor Yellow
    Write-Host 'so a session open across this rebuild keeps the old binary until it restarts.' -ForegroundColor Yellow
}
finally
{
    Pop-Location
    Remove-Item $NugetConfig -ErrorAction SilentlyContinue
}
