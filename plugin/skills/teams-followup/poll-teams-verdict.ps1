<#
.SYNOPSIS
    Watches one drafted message for the user's verdict and emits one stdout line when it lands.

.DESCRIPTION
    The detection half of the investigate -> react -> draft -> approve loop. A draft is posted to
    the user's self-chat and this script watches it for either of the two things the user can do
    next:

      - put a reaction on it        -> approval, forward the draft as-is
      - reply in the chat           -> feedback, revise and re-draft

    Both signals come from a single `read_chat_messages` call per tick, because the draft and any
    reply live in the same conversation: the reactions ride on the draft's own record and a reply
    is simply a message newer than it. So there is no cursor to keep and no second call to make -
    the draft's message id is the whole of the bookkeeping.

    Reactions are why this polls at all. The server's waiters key off a message's CreatedDateTime,
    and a reaction creates no message and does not move that timestamp, so wait_for_chat_messages
    can never fire on one. Replies alone would be watchable server-side; reactions are not, and the
    loop needs both.

    Runs `teams-mcp call` rather than talking to Graph, so the server loads its own auth record and
    this script never handles a credential. The child inherits TEAMS_MCP_ALLOW_SEND from the
    environment but only ever calls a read tool.

.PARAMETER MessageId
    Id of the drafted message to watch, as returned by send_chat_message.

.PARAMETER Chat
    Chat the draft was posted to. Defaults to the signed-in user's self-chat.

.PARAMETER SinceId
    Optional. Only messages newer than this id count as feedback. Defaults to -MessageId, which is
    what you want: the draft itself is the boundary.

.PARAMETER Window
    How many messages back to read each tick. The draft must stay inside it; if it falls out, the
    script says so rather than reporting silence.

.OUTPUTS
    TEAMS-VERDICT-READY     watching=<id> chat=<chat> interval=<n>s deadline=<utc>
    TEAMS-VERDICT-APPROVED  reaction=<emoji> by=<id> msg=<id>
    TEAMS-VERDICT-FEEDBACK  id=<id> at=<utc> :: <single-line body>
    TEAMS-VERDICT-QUIET     waited=<n>s (deadline reached, no verdict)
    TEAMS-VERDICT-GAP       <what may have been missed>
    TEAMS-VERDICT-ERR       <what went wrong>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MessageId,
    [string]$Chat = '48:notes',
    [string]$SinceId,
    [int]$IntervalSeconds = 15,
    [int]$MaxSeconds = 3600,
    [int]$Window = 20,
    [int]$BodyLimit = 400
)

$ErrorActionPreference = 'Stop'

# The emoji is the signal, not decoration - a mangled one costs the verdict its meaning. Both ends
# need saying: the child's stdout is UTF-8 JSON, and this script's own stdout is what Monitor reads.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

if (-not $SinceId) { $SinceId = $MessageId }
if ($IntervalSeconds -lt 5) { $IntervalSeconds = 5 }

$exe = (Get-Command teams-mcp -ErrorAction SilentlyContinue).Source
if (-not $exe) {
    Write-Output 'TEAMS-VERDICT-ERR teams-mcp not on PATH; the .NET tool is not installed'
    exit 1
}

$deadline = (Get-Date).ToUniversalTime().AddSeconds($MaxSeconds)
$started = Get-Date

function Read-Chat {
    # stdout is the result JSON; stderr is the server's log stream and is discarded.
    $raw = & $exe call read_chat_messages "chat=$Chat" "limit=$Window" "body_limit=$BodyLimit" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "read_chat_messages exited $LASTEXITCODE" }
    return ($raw -join "`n" | ConvertFrom-Json)
}

Write-Output ("TEAMS-VERDICT-READY watching={0} chat={1} interval={2}s deadline={3}" -f `
    $MessageId, $Chat, $IntervalSeconds, $deadline.ToString('yyyy-MM-ddTHH:mm:ssZ'))

$errs = 0
while ((Get-Date).ToUniversalTime() -lt $deadline) {
    try {
        $result = Read-Chat
        $errs = 0
    }
    catch {
        $errs++
        if ($errs -eq 1 -or $errs % 10 -eq 0) {
            Write-Output ("TEAMS-VERDICT-ERR {0} (consecutive: {1})" -f $_.Exception.Message, $errs)
        }
        Start-Sleep -Seconds $IntervalSeconds
        continue
    }

    $messages = @($result.messages)
    $draftAt = -1
    for ($i = 0; $i -lt $messages.Count; $i++) {
        if ($messages[$i].id -eq $MessageId) { $draftAt = $i; break }
    }

    if ($draftAt -lt 0) {
        # Not finding the draft means it fell out of the window, so a reaction on it would now be
        # invisible. Say so - silence would read as "no verdict yet". A reaction on the draft can
        # only lift it towards the top, so the window never has to grow to keep up with reactions;
        # it only has to outrun genuinely new messages.
        Write-Output ("TEAMS-VERDICT-GAP draft {0} is no longer within the newest {1} messages; " +
            "reactions on it can no longer be seen - re-arm with a larger -Window" -f $MessageId, $Window)
        exit 2
    }

    # Approval: any reaction on the draft. This is only unambiguous because the loop never reacts
    # to its own draft - the server acts as the signed-in user, so a reaction it placed would be
    # indistinguishable from the user's.
    $reactions = $result.messages[$draftAt].reactions
    if ($reactions) {
        # One verdict, one line, even when the client stacked several emoji: the API sets a single
        # reaction per user, but the Teams client itself can add more, and they mean one approval.
        $emoji = @($reactions.PSObject.Properties.Name) -join ' '
        $who = @($reactions.PSObject.Properties.Value | ForEach-Object { $_ } | Select-Object -Unique) -join ','
        Write-Output ("TEAMS-VERDICT-APPROVED reaction={0} by={1} msg={2}" -f $emoji, $who, $MessageId)
        exit 0
    }

    # Feedback: anything created after the draft - compared by timestamp, never by list position.
    # Graph orders chat messages by lastModifiedDateTime, not createdDateTime, and a reaction moves
    # lastModified: reacting to an older message lifts it above the draft in the listing without
    # anything new having been said. Measured here, by this script reporting its own test reaction
    # on an older message as a reply. A new message still sorts to the top (its lastModified is its
    # creation), so timestamp comparison loses nothing.
    # The two anchors are separate on purpose. Reactions are watched on the draft; feedback is
    # counted from -SinceId, which lets a re-arm step over messages already dealt with without
    # giving up the draft as the reaction target.
    $floorMsg = $messages | Where-Object { $_.id -eq $SinceId } | Select-Object -First 1
    if (-not $floorMsg) { $floorMsg = $messages[$draftAt] }
    $floor = [datetimeoffset]::Parse($floorMsg.created)
    $fresh = @($messages | Where-Object {
        $_.id -ne $MessageId -and $_.id -ne $SinceId -and
        [datetimeoffset]::Parse($_.created) -gt $floor
    } | Sort-Object { [datetimeoffset]::Parse($_.created) })
    if ($fresh.Count -gt 0) {
        foreach ($m in $fresh) {
            $body = ($m.body -replace '\s+', ' ').Trim()
            # An attachment-only message has no body at all, and reporting it as an empty line says
            # "they replied with nothing" - which is the one thing that did not happen. Name the
            # files instead; the reader can go fetch them.
            if ($m.attachments) {
                $names = @($m.attachments | ForEach-Object { $_.name }) -join ', '
                $body = if ($body) { "$body [attached: $names]" } else { "[attached: $names]" }
            }
            if (-not $body) { $body = '[no text and no attachment]' }
            # UTC and explicit about it. PowerShell renders a parsed timestamp in local time by
            # default, which reads as a plausible wrong answer rather than an obvious one.
            $at = [datetimeoffset]::Parse($m.created).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            Write-Output ("TEAMS-VERDICT-FEEDBACK id={0} at={1} :: {2}" -f $m.id, $at, $body)
        }
        exit 0
    }

    Start-Sleep -Seconds $IntervalSeconds
}

$waited = [int]((Get-Date) - $started).TotalSeconds
Write-Output ("TEAMS-VERDICT-QUIET waited={0}s" -f $waited)
exit 0
