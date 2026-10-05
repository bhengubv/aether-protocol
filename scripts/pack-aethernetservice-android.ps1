param(
    [string] $Configuration = 'Release',
    [string] $Version = '1.0',
    [int] $Code = 1,
    [switch] $AllowDebugSigned)
# Builds what SleptOn carries for Android: the AetherNetService package.
#
#   scripts/pack-aethernetservice-android.ps1 [-Configuration Release] [-Version 1.0] [-Code 1]
#
# 1. publishes AetherNetService for Android as an apk;
# 2. CHECKS WHO SIGNED IT, and refuses to call a debug-signed apk a release;
# 3. copies it to artifacts\android\AetherNetService-<version>.apk.
#
# Then, for a release, publish it so Aether can offer it to a phone that does not have it:
#   slepton publish artifacts\android\AetherNetService-<version>.apk --package com.bhengubv.aethernetservice --version <version> --code <code>
#
# The counterpart of pack-aethernetservice-windows.ps1. The Windows side had one of these and Android had none, so the
# Windows output was reproducible by one command and the Android output was whatever somebody last built by hand.
#
# WHY STEP 2 EXISTS, in the words of the csproj it is guarding (AetherNetService.csproj, above the signing block):
# "A Release build is signed with The Geek Network's key, or it is not a release: with no signing set, the SDK quietly
# signs with the debug key and the package installs, runs and can be shipped nowhere." The csproj closes that by
# naming the keystore and passing $(ANDROID_KEYSTORE_PASS), so an unset password fails the build with XA4314 - loud,
# and correct. This checks the other half: that what came out is signed by the key we meant, not by a debug key that
# somebody's keystore happened to supply. The apk is the thing that ships; asking the apk is the only answer that
# cannot be out of date.
#
# THE PASSWORD IS NOT AN ARGUMENT HERE, deliberately. It is never written down and never set machine-wide: Visual
# Studio's Archive signs with the key it manages, or a one-off command line passes it with -p:. Pass it the same way
# to this script's dotnet publish if you are building a release by hand:
#   $env:ANDROID_KEYSTORE_PASS = '...'   # for this shell only, then close it
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\android'
$publish = Join-Path $out 'publish'
$project = Join-Path $root 'src\AetherNetService\AetherNetService.csproj'

New-Item -ItemType Directory -Force $out | Out-Null

"1/3 publishing AetherNetService ($Configuration)"
# -m:1 is not a preference: a parallel Android publish of this graph runs the box out of memory.
# An apk and not an aab: SleptOn takes an apk, and an aab deploy uninstalls first, which costs a phone its identity.
dotnet publish $project -f net10.0-android -c $Configuration -m:1 `
    -p:AndroidPackageFormat=apk `
    -p:ApplicationDisplayVersion=$Version -p:ApplicationVersion=$Code `
    -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "AetherNetService did not publish" }

$apk = Get-ChildItem $publish -Filter '*-Signed.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $apk) { $apk = Get-ChildItem $publish -Filter '*.apk' | Select-Object -First 1 }
if (-not $apk) { throw "no apk came out of the publish" }

"2/3 checking who signed it"
# The newest build-tools on this machine; apksigner is the only thing that reads an apk's own signers.
$apksigner = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Android\Sdk\build-tools') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'apksigner.bat' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $apksigner) {
    throw "apksigner was not found under %LOCALAPPDATA%\Android\Sdk\build-tools - cannot say who signed this apk, so it is not shippable"
}

$certs = & $apksigner verify --print-certs $apk.FullName 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "apksigner could not verify $($apk.Name):`n$certs" }

$debugSigned = $certs -match 'CN=Android Debug'
$ourKey = $certs -match 'TheGeekAlias|The Geek'

"   $(($certs -split "`n" | Where-Object { $_ -match 'Signer #1 certificate DN' }) -join '; ')"

if ($debugSigned) {
    if (-not $AllowDebugSigned) {
        throw @"
This apk is signed with the ANDROID DEBUG KEY, not The Geek Network's.

It will install and it will run, and it can be shipped nowhere: a store and SleptOn both refuse a
debug-signed package, and a phone that already has the real one will refuse this as a different app.

The password is never stored. Sign it with Visual Studio's Archive, or pass it for one shell only:
  `$env:ANDROID_KEYSTORE_PASS = '...'
  scripts/pack-aethernetservice-android.ps1 -Version $Version -Code $Code

Pass -AllowDebugSigned only for a build going onto a test phone by hand, never for a release.
"@
    }
    "   WARNING: debug-signed, and -AllowDebugSigned was passed. This is a test build, not a release."
}
elseif (-not $ourKey) {
    "   WARNING: signed by a key this script does not recognise. Check the DN above before shipping it."
}

"3/3 copying it out"
$named = Join-Path $out "AetherNetService-$Version.apk"
Copy-Item $apk.FullName $named -Force
"done: $named ($([math]::Round((Get-Item $named).Length / 1MB, 1)) MB)$(if ($debugSigned) { ' - DEBUG-SIGNED, test only' })"
