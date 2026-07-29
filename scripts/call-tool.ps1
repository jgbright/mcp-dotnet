#Requires -Version 7
<#
.SYNOPSIS
Calls one tool on a stdio MCP server and prints its result.

.DESCRIPTION
Drives a server the way an MCP client does — initialize, initialized, tools/call — without needing
a client. That matters in two situations, both of which come up while changing a server:

- The client's connection to this server is gone. Reinstalling a tool kills the running server to
  release its DLLs, and a client does not necessarily relaunch one, so the tools disappear from the
  session that was using them until the client restarts. This still reaches the new binary.
- The call needs Debug logging. `-LogLevel Debug` is set on the child only and logs every HTTP
  request the server makes, which is the difference between "the write returned 200" and knowing
  what it actually sent. A server already launched by a client cannot be turned up this way,
  because it inherited its environment at client startup.

Three things about stdio MCP make this a script rather than a remembered pipeline, and two of them
look like a server that never answers:

- An un-drained stderr pipe fills and blocks the child. Redirecting stderr without reading it is
  enough to hang the whole exchange before a single response line arrives.
- Closing stdin ends the host. The transport shuts down on end-of-stream, so stdin has to stay open
  until the reply lands or an in-flight tool call dies with it.
- tools/call is rejected before the handshake. initialize and the initialized notification both
  have to go first.

.PARAMETER ToolArgs
The tool's arguments, as the hashtable form of its JSON object: @{ id = 7848; remove_tags = 'x' }.

.EXAMPLE
./call-tool.ps1 -Tool get_work_item -ToolArgs @{ id = 7848 }

.EXAMPLE
./call-tool.ps1 -Tool update_work_item -ToolArgs @{ id = 7848; remove_tags = 'stale-tag' }

.EXAMPLE
./call-tool.ps1 -Server Teams -Tool list_chats -ToolArgs @{ limit = 5 } -Raw

.NOTES
Writes are real. This talks to the same organization and the same signed-in identity the server
always does, so a tool that mutates something mutates it here too.
#>
[CmdletBinding()]
param(
    [ValidateSet('Ado', 'Teams')] [string]$Server = 'Ado',
    [Parameter(Mandatory)] [string]$Tool,
    [hashtable]$ToolArgs = @{},
    [string]$Exe,
    [ValidateSet('Trace', 'Debug', 'Information', 'Warning', 'Error', 'None')]
    [string]$LogLevel = 'Debug',
    [int]$TimeoutSeconds = 60,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

$Command = if ($Server -eq 'Teams') { 'teams-mcp' } else { 'ado-mcp' }
$LogLevelVar = if ($Server -eq 'Teams') { 'TEAMS_MCP_LOG_LEVEL' } else { 'ADO_MCP_LOG_LEVEL' }

if (-not $Exe)
{
    $resolved = Get-Command $Command -ErrorAction SilentlyContinue
    $Exe = if ($resolved) { $resolved.Source } else { Join-Path $env:USERPROFILE ".dotnet\tools\$Command.exe" }
}
if (-not (Test-Path $Exe))
{
    throw "Could not find $Command. Install it with scripts/rebuild.ps1, or pass -Exe."
}

$psi = [System.Diagnostics.ProcessStartInfo]@{
    FileName               = $Exe
    RedirectStandardInput  = $true
    RedirectStandardOutput = $true
    RedirectStandardError  = $true
    UseShellExecute        = $false
}
# On the child only. Turning up a server the client launched is not possible from here: it took its
# environment from the client at startup.
$psi.EnvironmentVariables[$LogLevelVar] = $LogLevel

$proc = [System.Diagnostics.Process]::new()
$proc.StartInfo = $psi
$null = $proc.Start()
# The child blocks once an un-drained stderr pipe fills, which is indistinguishable from a server
# that never answers. Draining it is what makes stdout readable at all.
$proc.BeginErrorReadLine()

function Send-Message($Message)
{
    $proc.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 24 -Compress))
    $proc.StandardInput.Flush()
}

try
{
    Send-Message @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params  = @{
            protocolVersion = '2024-11-05'
            capabilities    = @{}
            clientInfo      = @{ name = 'call-tool.ps1'; version = '1.0' }
        }
    }
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    Send-Message @{
        jsonrpc = '2.0'; id = 2; method = 'tools/call'
        params  = @{ name = $Tool; arguments = $ToolArgs }
    }

    # Read until the id=2 reply. Notifications and the initialize response come first and are of no
    # interest. Stdin stays open throughout, or the host shuts down mid-call.
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $reply = $null
    while ([datetime]::UtcNow -lt $deadline)
    {
        $remaining = [int][math]::Max(1, ($deadline - [datetime]::UtcNow).TotalMilliseconds)
        $read = $proc.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) { break }
        $line = $read.Result
        if ($null -eq $line) { break }
        if ($line -notmatch '"id"\s*:\s*2\b') { continue }
        $reply = $line | ConvertFrom-Json
        break
    }

    if (-not $reply)
    {
        throw "No reply to tools/call within $TimeoutSeconds seconds. The server's log file has the detail."
    }
    if ($Raw)
    {
        return $reply | ConvertTo-Json -Depth 24
    }
    if ($reply.error)
    {
        return $reply.error | ConvertTo-Json -Depth 24
    }
    # isError marks a tool that ran and refused, which carries its reason as text rather than
    # as structured output.
    if ($reply.result.isError)
    {
        return $reply.result.content.text
    }
    return $reply.result.structuredContent | ConvertTo-Json -Depth 24
}
finally
{
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
    $proc.Dispose()
}
