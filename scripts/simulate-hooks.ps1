<#
.SYNOPSIS
    Replays two simulated Claude Code turns against StatusHub.Service to check the DeskPet animations.

.DESCRIPTION
    Posts fake hook payloads to http://127.0.0.1:<Port>/hooks/<Event>, like plugin/scripts/forward-hook.sh.

    Turn 1 ends normally (Stop). It has slow tool calls with composing gaps, a tool failure,
    a permission notification and bursts of rapid events.
    Turn 2 starts while done is still locked on screen, has more rapid events and ends with
    a tool failure followed by StopFailure.

    Each line printed shows the elapsed time, the event and what the pet should do.
    The service also writes these events to its hook log; their session_id starts with "simulated-".

.PARAMETER Speed
    Time scale: 2 plays twice as fast. Keep it at 1 to judge the real timing.

.PARAMETER ThinkFallback
    Also wait past Status:ThinkFallback (default 45 s) between two tools in turn 2, to see working fall back to think.
#>
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 47821,
    [ValidateRange(0.1, 10)]
    [double]$Speed = 1.0,
    [switch]$ThinkFallback
)

$ErrorActionPreference = 'Stop'

$sessionId = 'simulated-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$baseUri = "http://127.0.0.1:$Port/hooks"
$clock = [System.Diagnostics.Stopwatch]::StartNew()

function Send-Hook([string]$EventName, [hashtable]$Fields = @{}, [string]$Expect = '') {
    $payload = @{
        session_id      = $sessionId
        hook_event_name = $EventName
        cwd             = (Get-Location).Path
        transcript_path = ''
    }
    foreach ($key in $Fields.Keys) { $payload[$key] = $Fields[$key] }
    $body = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 5 -Compress))
    Invoke-RestMethod -Method Post -Uri "$baseUri/$EventName" -ContentType 'application/json' -Body $body -TimeoutSec 2 | Out-Null
    Write-Host ('{0,7:N1}s  {1,-19} {2}' -f $clock.Elapsed.TotalSeconds, $EventName, $Expect)
}

function Wait-Ms([int]$Milliseconds) {
    Start-Sleep -Milliseconds ([int]($Milliseconds / $Speed))
}

function Write-Phase([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Tool([string]$Name) {
    $inputs = @{
        Read  = @{ file_path = 'src/DeskPet.App/MainWindow.xaml.cs' }
        Grep  = @{ pattern = 'PetController' }
        Glob  = @{ pattern = '**/*.cs' }
        Edit  = @{ file_path = 'src/DeskPet.App/MainWindow.xaml.cs'; old_string = 'a'; new_string = 'b' }
        Write = @{ file_path = 'tests/DeskPet.App.Tests/NewTests.cs'; content = '// test' }
        Bash  = @{ command = 'dotnet test' }
    }
    @{ tool_name = $Name; tool_input = $inputs[$Name] }
}

# Pre -> run -> Post, then the composing gap until the next call.
function Invoke-Tool([string]$Name, [int]$RunMs, [int]$ComposeMs, [string]$Expect = 'working, active') {
    Send-Hook 'PreToolUse' (Tool $Name) "$Name; $Expect"
    Wait-Ms $RunMs
    Send-Hook 'PostToolUse' (Tool $Name) 'working, composing (taps less often)'
    Wait-Ms $ComposeMs
}

# Tool calls only a few tens of milliseconds apart: the pet should stay on working without flicker.
function Invoke-RapidTools([string[]]$Names) {
    foreach ($name in $Names) {
        Send-Hook 'PreToolUse' (Tool $name) "$name (rapid)"
        Wait-Ms 25
        Send-Hook 'PostToolUse' (Tool $name) "$name done (rapid)"
        Wait-Ms 40
    }
}

try {
    Send-Hook 'SessionStart' @{ source = 'startup' } 'idle'
}
catch {
    Write-Host "Cannot reach StatusHub.Service at $baseUri ($($_.Exception.Message))." -ForegroundColor Red
    Write-Host 'Start the service first, or pass -Port if it listens elsewhere.'
    exit 1
}
Write-Host "Session $sessionId"
Wait-Ms 2000

Write-Phase 'Turn 1: ends normally'
Send-Hook 'UserPromptSubmit' @{ prompt = 'Add a test for PetController' } 'think'
Wait-Ms 3000
Invoke-Tool 'Read' 150 2500
Invoke-Tool 'Grep' 80 1800

Write-Phase 'Turn 1: rapid tool calls'
Invoke-RapidTools @('Read', 'Read', 'Glob', 'Read')
Wait-Ms 2000

Write-Phase 'Turn 1: a tool fails'
Send-Hook 'PreToolUse' (Tool 'Bash') 'Bash; working, active'
Wait-Ms 2500
$failure = (Tool 'Bash') + @{ error = 'Command exited with code 1'; is_interrupt = $false }
Send-Hook 'PostToolUseFailure' $failure 'sweat drop, stays working (composing)'
Wait-Ms 3000

Write-Phase 'Turn 1: permission prompt'
Send-Hook 'Notification' @{ message = 'Claude needs your permission to use Edit'; notification_type = 'permission_prompt' } 'notice'
Wait-Ms 4000
Invoke-Tool 'Edit' 100 2000
Invoke-Tool 'Write' 120 1500

Write-Phase 'Turn 1: finished'
Send-Hook 'Stop' @{ stop_hook_active = $false } 'done (15 s, locked for the first 2 s)'
Wait-Ms 1200

Write-Phase 'Turn 2: starts inside the done lock'
Send-Hook 'UserPromptSubmit' @{ prompt = 'Now run the tests' } 'still done until the lock ends'
Wait-Ms 300
Invoke-Tool 'Read' 50 3000 'still done; then working, composing once the lock and star loop end'

Write-Phase 'Turn 2: rapid mixed statuses'
$permission = @{ message = 'Claude needs your permission to use Bash'; notification_type = 'permission_prompt' }
Send-Hook 'Notification' $permission 'notice'
Wait-Ms 200
# All of these arrive while notice is still held (500 ms minimum plus its pop): only the last one should show.
Send-Hook 'PreToolUse' (Tool 'Bash') 'held'
Wait-Ms 60
Send-Hook 'PostToolUse' (Tool 'Bash') 'held'
Wait-Ms 60
Send-Hook 'Notification' $permission 'held'
Wait-Ms 60
Send-Hook 'PreToolUse' (Tool 'Bash') 'Bash; straight from notice to working once the pop finishes'
Wait-Ms 100
Send-Hook 'PostToolUse' (Tool 'Bash') 'working, composing'
Wait-Ms 2500
Invoke-RapidTools @('Glob', 'Read', 'Grep')
Wait-Ms 1500

if ($ThinkFallback) {
    Write-Phase 'Turn 2: long gap between tools'
    Invoke-Tool 'Read' 100 47000 'working, composing; think after Status:ThinkFallback'
}

Write-Phase 'Turn 2: stops on an error'
Invoke-Tool 'Bash' 1500 1200
Send-Hook 'PreToolUse' (Tool 'Bash') 'Bash; working, active'
Wait-Ms 800
$failure = (Tool 'Bash') + @{ error = 'Build failed'; is_interrupt = $false }
Send-Hook 'PostToolUseFailure' $failure 'sweat drop'
Wait-Ms 600
Send-Hook 'StopFailure' @{ error = 'rate_limit' } 'error (stays until the next prompt)'
Wait-Ms 3000

Send-Hook 'SessionEnd' @{ reason = 'other' } 'session removed; error stays on screen'
Write-Host ''
Write-Host 'Done. The pet stays on error until a real prompt is submitted.'
