# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  End-to-end chat test across two phones: Aether on phone A -> AetherNetService on A -> radio ->
  AetherNetService on B -> Aether on B, then a reply back the same way.

.DESCRIPTION
  Proves the thin client end to end. Each phone runs two separate apps — AetherNetService (identity, radios,
  secure sessions) and a DEBUG build of Aether (the thin client) — and the message has to cross from one app to
  the other on each phone, and over the air between them.

  Driven through Aether's DEBUG-only test hooks (E2eHooks.cs) and read back from logcat (tag AetherE2E). Passes
  only when each message is received on the far phone AND its sender sees it confirmed "delivered".

  Before running, on BOTH phones:
    - AetherNetService and a DEBUG build of Aether are installed;
    - AetherNetService has Location (Settings -> Apps -> AetherNetService -> Permissions) — its radios need it;
    - the phones are within Bluetooth range, or on the same Wi-Fi.

.EXAMPLE
  ./scripts/e2e-chat.ps1 -A UTKDU19919000815 -B 192.168.0.249:41234
#>
param(
    [Parameter(Mandatory)] [string] $A,
    [Parameter(Mandatory)] [string] $B,
    [int] $TimeoutSeconds = 180,
    [string] $Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
)

$ErrorActionPreference = 'Stop'
$Aether = 'com.bhengubv.aethernet'
$Service = 'com.bhengubv.aethernetservice'

function Adb([string] $serial) { & $Adb -s $serial @args 2>&1 }

function Activity([string] $serial) {
    $line = Adb $serial shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $Aether | Select-Object -Last 1
    if ($line -notmatch '/') { throw "Aether is not installed on $serial" }
    "$line".Trim()
}

function Hook([string] $serial, [string] $activity, [string[]] $extras) {
    Adb $serial shell am start -n $activity @extras | Out-Null
}

function E2eLines([string] $serial) { Adb $serial logcat -d -s 'AetherE2E:I' }

# Wait until a line matching $pattern appears in this phone's AetherE2E log; return the match, or $null on timeout.
function WaitFor([string] $serial, [string] $pattern, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $hit = E2eLines $serial | Select-String -Pattern $pattern | Select-Object -First 1
        if ($hit) { return $hit.Line }
        Start-Sleep -Seconds 2
    }
    return $null
}

function Precheck([string] $serial) {
    $packages = Adb $serial shell pm list packages
    foreach ($p in $Aether, $Service) {
        if (-not ($packages -match "package:$([regex]::Escape($p))$")) { throw "$p is not installed on $serial" }
    }
    $granted = Adb $serial shell dumpsys package $Service | Select-String 'ACCESS_FINE_LOCATION: granted=true'
    if (-not $granted) {
        Write-Warning "$serial : AetherNetService has no Location permission — its radios cannot find the other phone. Grant it in Settings -> Apps -> AetherNetService -> Permissions."
    }
}

function Fail([string] $why) {
    Write-Host "FAIL: $why" -ForegroundColor Red
    foreach ($s in $A, $B) {
        Write-Host "---- $s : AetherE2E ----"
        E2eLines $s | Select-Object -Last 20
        Write-Host "---- $s : AetherNetService ----"
        Adb $s logcat -d | Select-String 'AetherNetService/' | Select-Object -Last 20 | ForEach-Object { $_.Line }
    }
    exit 1
}

Write-Host "Checking both phones..."
Precheck $A; Precheck $B
$actA = Activity $A; $actB = Activity $B

Adb $A logcat -c | Out-Null; Adb $B logcat -c | Out-Null

Write-Host "Asking each phone who it is (its identity comes from its AetherNetService)..."
Hook $A $actA @('--es', 'e2e', 'whoami'); Hook $B $actB @('--es', 'e2e', 'whoami')
$meA = WaitFor $A 'me=(\S+)' 60; $meB = WaitFor $B 'me=(\S+)' 60
if (-not $meA -or -not $meB) { Fail "a phone did not say who it is (is AetherNetService installed?)" }
$tagA = ([regex]'me=(\S+)').Match($meA).Groups[1].Value
$tagB = ([regex]'me=(\S+)').Match($meB).Groups[1].Value
Write-Host "  A = $tagA   B = $tagB"

Write-Host "Adding each phone to the other's contacts..."
Hook $A $actA @('--es', 'e2e', 'add', '--es', 'tag', $tagB); Hook $B $actB @('--es', 'e2e', 'add', '--es', 'tag', $tagA)
if (-not (WaitFor $A "add tag=$tagB ok=" 30)) { Fail "A could not add B" }
if (-not (WaitFor $B "add tag=$tagA ok=" 30)) { Fail "B could not add A" }

$nonce = [guid]::NewGuid().ToString('N').Substring(0, 8)
$there = "e2e-$nonce-there"
$back = "e2e-$nonce-back"

Write-Host "A -> B : $there"
$t0 = Get-Date
Hook $A $actA @('--es', 'e2e', 'send', '--es', 'tag', $tagB, '--es', 'text', $there)
if (-not (WaitFor $B "recv .*from=$tagA text=$there" $TimeoutSeconds)) { Fail "B never received '$there'" }
Write-Host ("  received on B after {0:N0}s" -f ((Get-Date) - $t0).TotalSeconds)
if (-not (WaitFor $A "state .*peer=$tagB state=delivered" $TimeoutSeconds)) { Fail "A never saw '$there' confirmed delivered" }
Write-Host ("  confirmed delivered on A after {0:N0}s" -f ((Get-Date) - $t0).TotalSeconds)

Write-Host "B -> A : $back"
$t1 = Get-Date
Hook $B $actB @('--es', 'e2e', 'send', '--es', 'tag', $tagA, '--es', 'text', $back)
if (-not (WaitFor $A "recv .*from=$tagB text=$back" $TimeoutSeconds)) { Fail "A never received '$back'" }
Write-Host ("  received on A after {0:N0}s" -f ((Get-Date) - $t1).TotalSeconds)
if (-not (WaitFor $B "state .*peer=$tagA state=delivered" $TimeoutSeconds)) { Fail "B never saw '$back' confirmed delivered" }
Write-Host ("  confirmed delivered on B after {0:N0}s" -f ((Get-Date) - $t1).TotalSeconds)

Write-Host "PASS: a message crossed Aether -> AetherNetService -> radio -> AetherNetService -> Aether, both ways, and was confirmed delivered." -ForegroundColor Green
exit 0
