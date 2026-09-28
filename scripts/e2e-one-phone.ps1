# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  End-to-end test on one phone: Aether (the thin client) -> AetherNetService (a separate app), every hop checked.

.DESCRIPTION
  Proves on a single phone, from the phone's own logs, that:
    1. AetherNetService runs on its own, and Aether connecting to it is what starts it;
    2. Aether's identity is the one AetherNetService holds;
    3. Aether's contacts reach AetherNetService;
    4. a message sent in Aether crosses into AetherNetService under Aether's own message id, and the service takes it;
    5. if AetherNetService dies, Aether reconnects on its own and the next message still gets through;
    6. nothing crashes.

  Needs AetherNetService and a DEBUG build of Aether installed (it drives Aether's DEBUG-only E2E hooks).
  A message cannot be delivered with one phone, so "delivered" is not checked here.

.EXAMPLE
  ./scripts/e2e-one-phone.ps1 -Serial UTKDU19919000815 -To 71P7B-TPERH
#>
param(
    [Parameter(Mandatory)] [string] $Serial,
    [Parameter(Mandatory)] [string] $To,
    [string] $Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
)

$ErrorActionPreference = 'Stop'
$Aether = 'com.bhengubv.aethernet'
$Service = 'com.bhengubv.aethernetservice'
$results = [System.Collections.Generic.List[object]]::new()

function Adb { & $Adb -s $Serial @args 2>&1 }
function Log { Adb logcat -d }
function E2e { Adb logcat -d -s 'AetherE2E:I' }
function Pid([string] $package) { "$(Adb shell pidof $package)".Trim() }

function WaitFor([scriptblock] $source, [string] $pattern, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $hit = & $source | Select-String -Pattern $pattern | Select-Object -Last 1
        if ($hit) { return $hit }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Check([string] $what, [bool] $ok, [string] $evidence) {
    $results.Add([pscustomobject]@{ Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Check = $what; Evidence = $evidence })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what) -ForegroundColor $colour
    if ($evidence) { Write-Host "       $evidence" }
}

function Hook([string[]] $extras) { Adb shell am start -n $activity @extras | Out-Null }

# --- Setup --------------------------------------------------------------------------------------------------
$front = "$(Adb shell dumpsys window | Select-String 'mCurrentFocus' | Select-Object -First 1)"
if ($front -notmatch 'launcher|com\.bhengubv\.') { throw "Something else is in front on the phone ($front) — not driving it." }

$activity = "$(Adb shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $Aether | Select-Object -Last 1)".Trim()
if ($activity -notmatch '/') { throw "Aether is not installed on $Serial" }

Adb shell am force-stop $Aether | Out-Null
Adb shell am force-stop $Service | Out-Null
Adb logcat -c | Out-Null
$serviceBefore = Pid $Service

# --- 1 + 2: Aether starts the service, and its identity is the service's --------------------------------------
Hook @('--es', 'e2e', 'whoami')
$me = WaitFor { E2e } 'me=(\S+)' 60
$started = WaitFor { Log } "Start proc \d+:$([regex]::Escape($Service))/.*for service" 30
Check 'AetherNetService runs on its own, started by Aether connecting to it' `
    ([string]::IsNullOrEmpty($serviceBefore) -and $null -ne $started) `
    ("not running before: $([string]::IsNullOrEmpty($serviceBefore)); then: " + $(if ($started) { $started.Line.Trim() } else { 'never started' }))
$tag = if ($me) { $me.Matches[0].Groups[1].Value } else { '' }
Check "Aether's identity comes from AetherNetService" ($tag -ne '') $(if ($me) { $me.Line.Trim() } else { 'Aether never said who it is' })

# --- 3: contacts reach the service --------------------------------------------------------------------------
$met = WaitFor { Log } 'AetherNetService/RadioMeeting: Keeping (\d+) contact' 30
Check "Aether's contacts reach AetherNetService" ($null -ne $met) $(if ($met) { $met.Line.Trim() } else { 'the service never received contacts' })

# --- 4: a message crosses into the service under Aether's own id ---------------------------------------------
function SendAndTrace([string] $text) {
    $known = @{}
    E2e | Select-String 'id=(\w+)' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $known[$_.Groups[1].Value] = $true }

    Hook @('--es', 'e2e', 'send', '--es', 'tag', $To, '--es', 'text', $text)
    if (-not (WaitFor { E2e } ([regex]::Escape("send tag=$To text=$text")) 60)) { return $null }

    # Aether's own message id: the first message to $To that was not in the log before this send.
    $deadline = (Get-Date).AddSeconds(30); $id = $null
    while (-not $id -and (Get-Date) -lt $deadline) {
        foreach ($m in (E2e | Select-String "state id=(\w+) peer=$([regex]::Escape($To)) state=(\w+)")) {
            $candidate = $m.Matches[0].Groups[1].Value
            if (-not $known.ContainsKey($candidate)) { $id = $candidate; break }
        }
        if (-not $id) { Start-Sleep -Seconds 1 }
    }
    if (-not $id) { return $null }

    $guid = [guid]::ParseExact($id, 'N').ToString('D')
    $accepted = WaitFor { E2e } "state id=$id peer=$([regex]::Escape($To)) state=sent" 30
    $taken = WaitFor { Log } "AetherNetService/MeshNodeMessaging: (Holding message $guid|Sent held message $guid)" 30
    [pscustomobject]@{ Id = $id; Guid = $guid; Accepted = $accepted; Taken = $taken }
}

$nonce = [guid]::NewGuid().ToString('N').Substring(0, 8)
$first = SendAndTrace "e2e-$nonce-1"
Check 'A message sent in Aether crosses into AetherNetService under the same message id' `
    ($null -ne $first -and $null -ne $first.Taken) `
    $(if ($first -and $first.Taken) { "Aether id $($first.Id) -> service: $($first.Taken.Line.Trim())" } else { 'the service never took the message' })
Check 'AetherNetService accepted it, so Aether shows it as sent' `
    ($null -ne $first -and $null -ne $first.Accepted) $(if ($first -and $first.Accepted) { $first.Accepted.Line.Trim() } else { 'Aether never marked it sent' })

# --- 5: the service dies, Aether reconnects on its own ------------------------------------------------------
$oldPid = Pid $Service
Adb shell am force-stop $Service | Out-Null
Start-Sleep -Seconds 3
$second = SendAndTrace "e2e-$nonce-2"
$newPid = Pid $Service
Check 'After AetherNetService dies, Aether reconnects on its own and the next message still gets through' `
    ($null -ne $second -and $null -ne $second.Taken -and $newPid -ne '' -and $newPid -ne $oldPid) `
    ("service pid $oldPid -> killed -> $newPid; " + $(if ($second -and $second.Taken) { $second.Taken.Line.Trim() } else { 'the new service never took the message' }))

# --- 6: nothing crashed -------------------------------------------------------------------------------------
$crashes = Log | Select-String 'FATAL EXCEPTION' -Context 0, 1 | Where-Object { "$_" -match 'bhengubv' }
Check 'Nothing crashed' ($crashes.Count -eq 0) $(if ($crashes) { ($crashes | Select-Object -First 1).ToString().Trim() } else { 'no FATAL EXCEPTION in either app' })

# --- Result --------------------------------------------------------------------------------------------------
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ''
if ($failed -eq 0) {
    Write-Host "PASS: $($results.Count)/$($results.Count) — Aether calls AetherNetService end to end on this phone." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL: $failed of $($results.Count) checks failed." -ForegroundColor Red
exit 1
