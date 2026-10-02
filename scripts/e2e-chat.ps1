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
    - AetherNetService may find phones nearby (Aether -> Settings -> "Let AetherNet find phones nearby" opens its
      page): Nearby devices from Android 13, Nearby devices and Location on 12, Location before that;
    - Aether or the home screen is in front — the test will not start Aether over another app;
    - the phones are within Bluetooth range, or on the same Wi-Fi.

  It reads only the lines it logs itself. It never clears a phone's log: others may be reading it.

.EXAMPLE
  ./scripts/e2e-chat.ps1 -A UTKDU19919000815 -B 192.168.0.249:41234
#>
param(
    [Parameter(Mandatory)] [string] $A,
    [Parameter(Mandatory)] [string] $B,
    [int] $TimeoutSeconds = 180,
    [string] $Adb = "${env:ProgramFiles(x86)}\Android\android-sdk\platform-tools\adb.exe"   # Visual Studio's
)

$ErrorActionPreference = 'Stop'
$Aether = 'com.bhengubv.aethernet'
$Service = 'com.bhengubv.aethernetservice'

# The phone is the first argument, not a named parameter: PowerShell matches a parameter by prefix, so logcat's
# own -s was taken as this function's -serial and adb was handed the phone's id as its command.
function Adb { $phone, $rest = $args; & $Adb -s $phone @rest 2>&1 }

function Activity([string] $serial) {
    $line = Adb $serial shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $Aether | Select-Object -Last 1
    if ($line -notmatch '/') { throw "Aether is not installed on $serial" }
    "$line".Trim()
}

function Hook([string] $serial, [string] $activity, [string[]] $extras) {
    Adb $serial shell am start -n $activity @extras | Out-Null
}

# Each phone's own clock when this run began, in seconds since the epoch — what logcat -T takes. Reading from there
# keeps an earlier run's lines from passing this one, without clearing a log somebody else may be reading.
$Since = @{}
function MarkStart([string] $serial) { $Since[$serial] = "$("$(Adb $serial shell date +%s)".Trim()).000" }
function E2eLines([string] $serial) { Adb $serial logcat -d -T $Since[$serial] -s 'AetherE2E:I' }

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
    # The test starts Aether on this phone. Never over somebody else's app.
    $front = "$(Adb $serial shell dumpsys window | Select-String 'mCurrentFocus' | Select-Object -First 1)".Trim()
    if ($front -notmatch 'com\.bhengubv\.aethernet/|[Ll]auncher|NexusLauncher|quickstep') { throw "$serial : not starting Aether over what is in front ($front)" }

    # What the radios wait on depends on the Android version — the same rule as RadioPermissions.
    $sdk = [int]"$(Adb $serial shell getprop ro.build.version.sdk)".Trim()
    $needed = if ($sdk -ge 33) { @('NEARBY_WIFI_DEVICES') } elseif ($sdk -ge 31) { @('BLUETOOTH_SCAN', 'ACCESS_FINE_LOCATION') } else { @('ACCESS_FINE_LOCATION') }
    $holds = Adb $serial shell dumpsys package $Service
    foreach ($p in $needed) {
        if (-not ($holds | Select-String "android.permission.${p}: granted=true")) {
            Write-Warning "$serial : AetherNetService has not been allowed $p — its radios cannot find the other phone. Allow it from Aether: Settings -> Let AetherNet find phones nearby."
        }
    }
}

function Fail([string] $why) {
    Write-Host "FAIL: $why" -ForegroundColor Red
    foreach ($s in $A, $B) {
        Write-Host "---- $s : AetherE2E ----"
        E2eLines $s | Select-Object -Last 20
        Write-Host "---- $s : AetherNetService ----"
        Adb $s logcat -d -T $Since[$s] | Select-String 'AetherNetService/' | Select-Object -Last 20 | ForEach-Object { $_.Line }
    }
    exit 1
}

Write-Host "Checking both phones..."
Precheck $A; Precheck $B
$actA = Activity $A; $actB = Activity $B

MarkStart $A; MarkStart $B

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
