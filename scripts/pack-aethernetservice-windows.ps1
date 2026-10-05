param(
    [string] $Configuration = 'Release',
    [string] $Version = '1.0',
    [int] $Code = 1)
# Builds what SleptOn carries for Windows: AetherNetService's setup program, with AetherNetService inside it.
#
#   scripts/pack-aethernetservice-windows.ps1 [-Configuration Release] [-Version 1.0] [-Code 1]
#
# 1. publishes AetherNetService for Windows, needing nothing on the computer (.NET and the Windows App SDK inside);
# 2. zips that folder;
# 3. builds the setup (src/AetherNetService.Setup) as one file with the zip inside it.
#
# Then, for a release: sign AetherNetService-Setup-<version>.exe — and AetherNetService.exe before step 2 — with the
# makers' code-signing certificate, the same one Aether.exe is signed with (Aether's check pins the signer to its own);
# and publish it, so Aether can offer it to a computer that does not have it:
#   slepton publish artifacts\windows\AetherNetService-Setup-<version>.exe --package com.bhengubv.aethernetservice --version <version> --code <code>
# SleptOn takes a .exe as the Windows build of the package.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\windows'
$service = Join-Path $out 'AetherNetService'
$zip = Join-Path $out 'AetherNetService.zip'
$setupOut = Join-Path $out 'setup'

if (Test-Path $service) { Remove-Item $service -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

"1/3 publishing AetherNetService ($Configuration, self-contained)"
# No -r here: a runtime named on the command line reaches every project the service references, the Android ones
# included, and they have no Windows runtime. The Windows head picks win-x64 itself.
dotnet publish (Join-Path $root 'src\AetherNetService\AetherNetService.csproj') -f net10.0-windows10.0.19041.0 -c $Configuration `
    -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None `
    -p:ApplicationDisplayVersion=$Version -p:ApplicationVersion=$Code -o $service --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "AetherNetService did not publish" }

"2/3 zipping it"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $service '*') -DestinationPath $zip

"3/3 building the setup with it inside"
dotnet publish (Join-Path $root 'src\AetherNetService.Setup\AetherNetService.Setup.csproj') -c $Configuration `
    -p:ServiceZip=$zip -o $setupOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "the setup did not build" }

$setup = Join-Path $out "AetherNetService-Setup-$Version.exe"
Copy-Item (Join-Path $setupOut 'AetherNetService-Setup.exe') $setup -Force
"done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
