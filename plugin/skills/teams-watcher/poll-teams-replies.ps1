<#
.SYNOPSIS
    Watches one or more Teams chats and emits one stdout line per new message.

.DESCRIPTION
    The detection half of the `teams-watcher` skill. Drives teams-mcp.exe as a
    stdio JSON-RPC child rather than talking to Graph directly, so the server
    loads its own Azure.Identity auth record and this script never handles a
    credential.

    The child is launched WITHOUT TEAMS_MCP_ALLOW_SEND, so its send tools are
    disabled. The poller can read and nothing else.

    Two design points carry the "only new messages" guarantee:

    - Scope is resolved from a list_chats call, and then the server does the
      waiting: one wait_for_chat_messages call watches every target chat
      concurrently and blocks until something arrives or its timeout lapses.
      The Graph polling, the merge, and the change detection all live
      server-side, so this script makes no per-tick calls at all. Scope is
      re-resolved when a wait returns and the last listing is older than
      -RefreshSeconds, so a chat that appears mid-watch is picked up without
      a restart.
    - The server's cursor token is the whole of resuming: every wait returns a
      nextCursor that the next call passes back, and the server guarantees
      nothing is re-delivered - no seen-id ring, no cursor file. The token
      lives in process memory; an -ExitOnBatch run prints its final token as a
      TEAMS-WATCH-CURSOR line so a relaunch can pass it back via -Cursor and
      resume without replaying.

    Messages authored by the signed-in user are dropped by default. A watcher
    exists to notice other people's replies, and relaying the lead's own
    outbound back to it is a feedback loop.

.OUTPUTS
    TEAMS-WATCH-READY  scope=<what> chats=<n> self='<name>' interval=<n>s
    TEAMS-REPLY        chat=<id> conv='<label>' from='<sender>' id=<msgid> at=<utc> :: <single-line body>
    TEAMS-WATCH-GAP    <what may have been missed>
    TEAMS-WATCH-QUIET  waited=<n>s (ExitOnBatch mode only)
    TEAMS-WATCH-CURSOR <token> (ExitOnBatch mode only, on exit)
    TEAMS-WATCH-ERR    <what went wrong>

.PARAMETER Chat
    Explicit chat ids to watch. Always watched, even when they fall outside
    the list_chats window.

.PARAMETER Member
    Watch every chat that includes a member whose display name contains this
    (case-insensitive). The "everything involving this person" scope.

.PARAMETER Topic
    Watch every chat whose topic contains this (case-insensitive). The
    "named set" scope.

.PARAMETER All
    Watch every chat in the list_chats window, capped at the 20 most recently
    active (one wait call covers at most 20 chats).

.PARAMETER ExitOnBatch
    Exit 0 as soon as at least one TEAMS-REPLY has been emitted, or after
    -MaxWaitSeconds with nothing. This is the mode the teams-watcher agent
    uses: the process exit is what wakes the agent up. Omit it to stream
    forever, which is the mode Monitor wants.

.PARAMETER Cursor
    An opaque token from a previous run's TEAMS-WATCH-CURSOR line. Resumes
    exactly where that run stopped instead of starting from now.

.PARAMETER WaitSeconds
    How long one wait_for_chat_messages call may block server-side before
    returning empty. The heartbeat of the loop: scope refresh and the
    ExitOnBatch clock are checked each time a wait returns.

.PARAMETER RefreshSeconds
    Re-resolve scope from list_chats when the last listing is at least this
    old and a wait has just returned. The price of a new chat entering the
    watch is one listing call at most this often.

.NOTES
    Scope covers chats, not team channels. wait_for_channel_messages needs a
    team+channel pair, which none of the four scopes above can resolve, so
    channels would be a separate parameter and a separate wait path rather
    than an extension of this one. Not built until something needs it.
#>
[CmdletBinding()]
param(
    [String[]] $Chat              = @(),
    [String[]] $Member            = @(),
    [String[]] $Topic             = @(),
    [Switch]   $All,
    [Switch]   $ExitOnBatch,
    [Int32]    $MaxWaitSeconds    = 900,
    [Int32]    $IntervalSeconds   = 15,
    [Int32]    $WaitSeconds       = 240,
    [Int32]    $RefreshSeconds    = 300,
    [Int32]    $Backfill          = 0,
    [Int32]    $MaxChats          = 50,
    [Int32]    $ReadLimit         = 50,
    [Int32]    $BodyLimit         = 4000,
    [String]   $SelfName          = '',
    [Switch]   $IncludeSelf,
    [String]   $Cursor            = '',
    [Int32]    $RpcTimeoutSeconds = 40,
    [String]   $ServerPath        = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

# How many chats one wait call may cover; mirrors the server's own cap.
$script:MaxWaitChats = 20

# ------------------------------------------------------------ path resolution

if ([String]::IsNullOrWhiteSpace($ServerPath))
{
    # The tool is on PATH once `dotnet tool install --global TeamsMcp` has run.
    # Resolving it here rather than leaning on Process.Start's own PATH search
    # turns "not installed" into a named failure instead of a bare Win32
    # "cannot find the file specified" from somewhere deeper in the script.
    $found = @(Get-Command 'teams-mcp' -CommandType Application -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0)
    {
        [Console]::Out.WriteLine('TEAMS-WATCH-ERR teams-mcp not found on PATH: install it with `dotnet tool install --global TeamsMcp`, or pass -ServerPath')
        exit 1
    }
    $ServerPath = $found[0].Source
}

if ($Chat.Count -eq 0 -and $Member.Count -eq 0 -and $Topic.Count -eq 0 -and -not $All)
{
    [Console]::Out.WriteLine('TEAMS-WATCH-ERR no scope given: pass -Chat, -Member, -Topic, or -All')
    exit 1
}

# ---------------------------------------------------------------- emit helpers

function Emit([String] $line)
{
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

$script:ErrorStreak = 0

# Rate-limited: first failure, then every 10th, so a sustained Graph outage does
# not become a notification firehose. Returns whether it actually spoke, so
# callers can stay silent in lockstep.
function EmitError([String] $what)
{
    $script:ErrorStreak++
    $speak = ($script:ErrorStreak -eq 1 -or $script:ErrorStreak % 10 -eq 0)
    if ($speak)
    {
        Emit "TEAMS-WATCH-ERR $what (consecutive: $($script:ErrorStreak))"
    }
    return $speak
}

# ------------------------------------------------------------------ mcp client

$script:Proc          = $null
$script:NextId        = 1
$script:PendingRead   = $null
$script:TimeoutStreak = 0

function Stop-Server
{
    if ($null -ne $script:Proc)
    {
        try { if (-not $script:Proc.HasExited) { $script:Proc.Kill() } } catch { }
        try { $script:Proc.Dispose() } catch { }
        $script:Proc = $null
    }
}

function Start-Server
{
    Stop-Server

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $ServerPath
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    # stderr is deliberately NOT redirected. The server logs about 1 KB per
    # request to it, and a redirected pipe nobody drains fills at roughly the
    # 4th request, at which point the server blocks mid-write and never sends
    # its response. Inheriting the parent's stderr cannot deadlock. The same
    # log content is on disk under %LOCALAPPDATA%\teams-mcp\logs\ anyway.
    $psi.RedirectStandardError  = $false
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.Environment['TEAMS_MCP_TENANT_ID'] = [Environment]::GetEnvironmentVariable('TEAMS_MCP_TENANT_ID', 'User')
    $psi.Environment['TEAMS_MCP_CLIENT_ID'] = [Environment]::GetEnvironmentVariable('TEAMS_MCP_CLIENT_ID', 'User')
    # TEAMS_MCP_ALLOW_SEND is deliberately absent: read-only by construction.

    $script:Proc        = [System.Diagnostics.Process]::Start($psi)
    $script:NextId      = 1
    $script:PendingRead = $null   # new reader, so any orphaned read is gone with it

    $init = Invoke-Rpc 'initialize' @{
        protocolVersion = '2024-11-05'
        capabilities    = @{}
        clientInfo      = @{ name = 'teams-watcher'; version = '1.0' }
    }
    if ($null -eq $init) { throw 'initialize returned nothing' }

    Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
}

function Send-Rpc($payload)
{
    $json = $payload | ConvertTo-Json -Depth 12 -Compress
    $script:Proc.StandardInput.WriteLine($json)
    $script:Proc.StandardInput.Flush()
}

# A timed-out read must NOT be abandoned. StreamReader allows only one read in
# flight, so starting a second while the first is pending throws or interleaves.
# The pending task is therefore held across calls and resumed on the next one.
function Read-RpcLine([Int32] $TimeoutMs)
{
    if ($null -eq $script:PendingRead)
    {
        $script:PendingRead = $script:Proc.StandardOutput.ReadLineAsync()
    }

    if (-not $script:PendingRead.Wait([Math]::Max(1, $TimeoutMs)))
    {
        return $null      # still in flight; kept for the next call to await
    }

    $line = $script:PendingRead.Result
    $script:PendingRead = $null
    if ($null -eq $line) { throw 'server closed stdout' }
    return $line
}

function Invoke-Rpc([String] $method, $parameters, [Int32] $TimeoutMs = 40000)
{
    $id = $script:NextId++
    Send-Rpc @{ jsonrpc = '2.0'; id = $id; method = $method; params = $parameters }

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        $remaining = [Int32]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        $line = Read-RpcLine $remaining
        if ($null -eq $line) { return $null }
        if ($line -notmatch '^\s*\{')  { continue }

        try   { $msg = $line | ConvertFrom-Json }
        catch { continue }

        # Notifications, and late answers to a call that already timed out, are
        # not ours. Discard and keep reading.
        if ($null -eq $msg.id -or [Int32]$msg.id -ne $id) { continue }
        if ($null -ne $msg.error) { throw "rpc error: $($msg.error.message)" }
        return $msg
    }

    return $null
}

# The server carries its payload in structuredContent and leaves content[]
# empty, so reading content[0].text yields null for every call - which looks
# exactly like "nothing was said" rather than like a parse failure. content[0]
# is kept as a fallback for an older server build.
#
# A tool whose return is not a JSON object arrives wrapped in a sole "result"
# property (list_chats), so that one is unwrapped. An object-returning tool
# (wait_for_chat_messages, with messages/nextCursor) is already the payload.
#
# Finding NEITHER field is a wire-format failure and throws. A well-formed
# empty answer is an empty list inside a payload that exists; a response with
# no payload at all means this client no longer understands the server, and
# reading that as emptiness is exactly what once kept a dead watch reporting
# READY through a wire-format change.
function Get-ToolPayload($resp)
{
    if ($resp.result.isError)
    {
        throw "tool call failed: $($resp.result.content[0].text)"
    }

    $payload = $resp.result.structuredContent

    if ($null -eq $payload)
    {
        $text = $resp.result.content[0].text
        if ([String]::IsNullOrWhiteSpace($text))
        {
            throw 'tool result carried neither structuredContent nor content[0].text; the server and this poller disagree on the wire format'
        }
        $payload = $text | ConvertFrom-Json
    }

    $names = @($payload.PSObject.Properties.Name)
    if ($names.Count -eq 1 -and $names[0] -eq 'result') { return $payload.result }
    return $payload
}

# Returns the chat listing: id, topic (absent on 1:1), type, lastUpdated,
# members[]. Deliberately unfiltered - one listing serves every scope, because
# member/topic matching is plain case-insensitive substring work that is
# cheaper to do here than as extra round trips.
function Get-Chats
{
    $resp = Invoke-Rpc 'tools/call' @{
        name      = 'list_chats'
        arguments = @{ limit = $MaxChats }
    } ($RpcTimeoutSeconds * 1000)

    if ($null -eq $resp) { throw 'list_chats timed out' }

    $payload = Get-ToolPayload $resp
    if ($null -eq $payload) { return @() }
    return @($payload)
}

# One blocking wait over every target chat. The server polls Graph at
# poll_seconds, merges newest-first across the chats, and returns as soon as
# anything arrives or timeout_seconds lapses; either way the result carries the
# nextCursor to resume from. A result without one is an old server whose waiter
# cannot resume, and silently replaying under it is worse than refusing.
function Invoke-Wait([String[]] $chatIds, [String] $cursorToken, [String] $since, [Int32] $timeoutSeconds)
{
    $arguments = @{
        chats           = @($chatIds)
        timeout_seconds = $timeoutSeconds
        poll_seconds    = $IntervalSeconds
        limit           = $ReadLimit
        body_limit      = $BodyLimit
    }
    if     (-not [String]::IsNullOrWhiteSpace($cursorToken)) { $arguments.cursor = $cursorToken }
    elseif (-not [String]::IsNullOrWhiteSpace($since))       { $arguments.since  = $since }

    $resp = Invoke-Rpc 'tools/call' @{
        name      = 'wait_for_chat_messages'
        arguments = $arguments
    } (($timeoutSeconds + $RpcTimeoutSeconds) * 1000)

    if ($null -eq $resp) { throw 'wait_for_chat_messages timed out' }

    $payload = Get-ToolPayload $resp
    if ($null -eq $payload -or $null -eq $payload.PSObject.Properties['nextCursor'])
    {
        throw 'wait_for_chat_messages result has no nextCursor; the server is too old to resume a watch'
    }
    return $payload
}

# -------------------------------------------------------------- scope + labels

# The signed-in user is the display name appearing in most of their own chats.
# Derived rather than configured so the watcher needs no identity of its own.
#
# Deliberately "most", not "all": the listing includes a few chats the user is
# not a member of, so intersecting every member list empties the set and yields
# nothing. A strict majority plus a unique maximum is what actually holds - the
# owner sat in 48 of 50 chats where the next most frequent person sat in 25.
function Resolve-Self($listed)
{
    if (-not [String]::IsNullOrWhiteSpace($SelfName)) { return $SelfName }

    $withMembers = @($listed | Where-Object { $null -ne $_.members -and @($_.members).Count -gt 0 })
    if ($withMembers.Count -lt 3) { return '' }

    $counts = @{}
    foreach ($c in $withMembers)
    {
        foreach ($person in @($c.members))
        {
            $name = [String]$person
            $counts[$name] = 1 + [Int32]$counts[$name]
        }
    }

    $top = @($counts.GetEnumerator() | Sort-Object -Property Value -Descending)
    if ($top[0].Value * 2 -le $withMembers.Count)            { return '' }   # no majority
    if ($top.Count -gt 1 -and $top[1].Value -eq $top[0].Value) { return '' }   # tied, so unsafe
    return $top[0].Key
}

function Get-Label($chatMeta, [String] $chatId, [String] $self)
{
    if ($null -eq $chatMeta) { return $chatId }
    if (-not [String]::IsNullOrWhiteSpace($chatMeta.topic)) { return [String]$chatMeta.topic }

    $others = @($chatMeta.members | Where-Object { $_ -ne $self })
    if ($others.Count -gt 0) { return ($others -join ', ') }
    return $chatId
}

function Test-InScope($chatMeta)
{
    if ($All) { return $true }

    foreach ($m in $Member)
    {
        foreach ($person in @($chatMeta.members))
        {
            if ($person -like "*$m*") { return $true }
        }
    }

    foreach ($t in $Topic)
    {
        if (-not [String]::IsNullOrWhiteSpace($chatMeta.topic) -and $chatMeta.topic -like "*$t*")
        {
            return $true
        }
    }

    return $false
}

# Resolves the target set from one listing: explicit ids first, so a chat named
# outright is still watched when it has fallen outside the most-recently-active
# listing window, then everything the scope matches, in listing order (most
# recently active first). Capped at the wait call's own limit - for a scope
# wider than that, the most recently active chats win and the trim is announced
# once rather than silently.
function Resolve-Scope
{
    $listed = Get-Chats

    if ([String]::IsNullOrWhiteSpace($script:Self))
    {
        $script:Self = Resolve-Self $listed
    }

    $script:ById = @{}
    foreach ($c in $listed) { $script:ById[[String]$c.id] = $c }

    $targets = [System.Collections.Specialized.OrderedDictionary]::new()
    foreach ($id in $Chat)
    {
        if (-not $targets.Contains($id)) { $targets[$id] = $script:ById[$id] }
    }
    foreach ($c in $listed)
    {
        $id = [String]$c.id
        if ($targets.Contains($id)) { continue }
        if (Test-InScope $c)        { $targets[$id] = $c }
    }

    # A signed-in user with zero chats does not happen in practice, so an -All
    # scope resolving to nothing means this client is broken, not that Teams is
    # empty. Throwing routes it to TEAMS-WATCH-ERR instead of letting a dead
    # watch announce READY.
    if ($All -and $targets.Count -eq 0)
    {
        throw 'scope -All resolved to zero chats; a signed-in user has chats, so the client or server is broken'
    }

    if ($targets.Count -gt $script:MaxWaitChats)
    {
        if (-not $script:TrimAnnounced)
        {
            Emit ("TEAMS-WATCH-GAP scope resolved to {0} chats but one wait covers {1}; watching the {1} most recently active, the rest join on a refresh when they surface" -f `
                  $targets.Count, $script:MaxWaitChats)
            $script:TrimAnnounced = $true
        }
        $trimmed = [System.Collections.Specialized.OrderedDictionary]::new()
        foreach ($id in $targets.Keys)
        {
            if ($trimmed.Count -ge $script:MaxWaitChats) { break }
            $trimmed[$id] = $targets[$id]
        }
        $targets = $trimmed
    }

    $script:LastResolve = [DateTime]::UtcNow
    return $targets
}

function Format-Body([String] $body)
{
    # Whitespace normalisation only. One event is one stdout line, so embedded
    # newlines would split a message in two, and Teams' paragraph-spacing
    # entities would otherwise show up as literal noise in the relay.
    $one = $body -replace '&nbsp;|&#160;', ' '

    # Blank lines are collapsed BEFORE the separator goes in, never after.
    # Collapsing afterwards means matching runs of " / ", and every pattern
    # loose enough to catch those also eats the "//" in a URL scheme, which
    # turns https://dev.azure.com/... into "https: / dev.azure.com/...".
    $one = $one -replace '(\r?\n\s*){2,}', "`n"
    $one = $one -replace '\r?\n', ' / '
    $one = $one -replace '[ \t]{2,}', ' '
    return $one.Trim()
}

# ------------------------------------------------------------------- main loop

$scopeDesc = @(
    if ($All)               { 'all' }
    if ($Chat.Count)        { "chats:$($Chat.Count)" }
    if ($Member.Count)      { "member:$($Member -join '|')" }
    if ($Topic.Count)       { "topic:$($Topic -join '|')" }
) -join ' '

try
{
    Start-Server
}
catch
{
    Emit "TEAMS-WATCH-ERR could not start teams-mcp server: $($_.Exception.Message)"
    exit 1
}

$script:Self          = $SelfName
$script:ById          = @{}
$script:LastResolve   = [DateTime]::MinValue
$script:TrimAnnounced = $false

$announced   = $false
$emitted     = 0
$started     = [DateTime]::UtcNow
$targets     = $null
$cursorToken = $Cursor
$draining    = $false

# Only the first wait uses `since`; from then on the cursor carries the exact
# position, ids included. No backfill means "from now", which the server's own
# default already is - passing nothing is the same request.
$firstSince = if ($Backfill -gt 0) { [DateTimeOffset]::UtcNow.AddMinutes(-$Backfill).ToUniversalTime().ToString('o') } else { '' }

try
{
    while ($true)
    {
        try
        {
            # Refresh is deferred while draining a hasMore backlog: those calls
            # return instantly and the point is to empty the buffer, not to
            # re-plan the watch between pages.
            $stale = ([DateTime]::UtcNow - $script:LastResolve).TotalSeconds -ge $RefreshSeconds
            if ($null -eq $targets -or ($stale -and -not $draining))
            {
                $targets = Resolve-Scope
            }

            if (-not $announced)
            {
                $selfLabel = if ([String]::IsNullOrWhiteSpace($script:Self)) { '<undetermined>' } else { $script:Self }
                Emit ("TEAMS-WATCH-READY scope={0} chats={1} self='{2}' interval={3}s" -f `
                      $scopeDesc, $targets.Count, $selfLabel, $IntervalSeconds)

                if ([String]::IsNullOrWhiteSpace($script:Self) -and -not $IncludeSelf)
                {
                    Emit 'TEAMS-WATCH-ERR could not determine the signed-in user; own messages will be relayed too. Pass -SelfName to filter them.'
                }
                $announced = $true
            }

            $timeoutSeconds = $WaitSeconds
            if ($ExitOnBatch)
            {
                $remaining = $MaxWaitSeconds - [Int32]([DateTime]::UtcNow - $started).TotalSeconds
                if ($remaining -le 0)
                {
                    Emit ("TEAMS-WATCH-QUIET waited={0}s" -f [Int32]([DateTime]::UtcNow - $started).TotalSeconds)
                    break
                }
                $timeoutSeconds = [Math]::Max(1, [Math]::Min($WaitSeconds, $remaining))
            }

            $wait = Invoke-Wait @($targets.Keys) $cursorToken $firstSince $timeoutSeconds
            $script:ErrorStreak   = 0
            $script:TimeoutStreak = 0

            # The cursor comes back on a timeout too; passing it forward is the
            # entire resume mechanism, so it is taken before anything else.
            $cursorToken = [String]$wait.nextCursor
            $firstSince  = ''

            $fresh = @($wait.messages | Sort-Object { [DateTimeOffset]::Parse($_.created) })
            foreach ($m in $fresh)
            {
                $sender = [String]$m.sender
                if (-not $IncludeSelf -and
                    -not [String]::IsNullOrWhiteSpace($script:Self) -and
                    $sender -eq $script:Self)
                {
                    continue
                }

                # chatId is only present when more than one chat is watched; a
                # single-target watch already knows which chat answered.
                $chatId = if ($null -ne $m.chatId) { [String]$m.chatId } else { [String]@($targets.Keys)[0] }
                $body   = Format-Body ([String]$m.body)
                if ($m.truncated) { $body = "$body [truncated]" }

                Emit ("TEAMS-REPLY chat={0} conv='{1}' from='{2}' id={3} at={4} :: {5}" -f `
                      $chatId,
                      (Get-Label $script:ById[$chatId] $chatId $script:Self),
                      $sender,
                      [String]$m.id,
                      [DateTimeOffset]::Parse($m.created).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ssZ'),
                      $body)
                $emitted++
            }

            # hasMore means at least one chat was cut out of this answer and
            # kept its place; call straight back with the cursor to drain it.
            $draining = [Boolean]$wait.hasMore
            if ($draining) { continue }

            if ($ExitOnBatch -and $emitted -gt 0) { break }
        }
        catch
        {
            $draining = $false
            $problem  = $_.Exception.Message
            $spoke    = EmitError $problem

            if ($problem -match 'timed out') { $script:TimeoutStreak++ }
            else                             { $script:TimeoutStreak = 0 }

            # Rebuild the child when it is gone, and also when a Graph call has
            # hung twice running: a fresh process costs ~150ms and clears both
            # the wedged request and any read still in flight against it. The
            # cursor token survives in this process, so a rebuilt child resumes
            # without replaying.
            if ($problem -match 'closed stdout|Pipe|handle is invalid' -or
                $script:TimeoutStreak -ge 2 -or
                $null -eq $script:Proc -or $script:Proc.HasExited)
            {
                try
                {
                    Start-Server
                    if ($spoke) { Emit 'TEAMS-WATCH-ERR server restarted, watch resumed' }
                }
                catch { [void] (EmitError "restart failed: $($_.Exception.Message)") }
            }

            # The server does the waiting on the happy path; only an error loop
            # needs its own pacing.
            Start-Sleep -Seconds $IntervalSeconds
        }
    }

    # Only ExitOnBatch reaches here, and its exit is a handoff: the token lets
    # the next arm resume where this one stopped instead of starting from now.
    if (-not [String]::IsNullOrWhiteSpace($cursorToken))
    {
        Emit "TEAMS-WATCH-CURSOR $cursorToken"
    }
}
finally
{
    Stop-Server
}
