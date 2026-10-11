<#
.SYNOPSIS
    Builds the plugin and packs the archive a player unpacks into the game folder.

.DESCRIPTION
    1. Runs Tools/ProtocolSmoke: a release with a broken wire layer is not packed.
    2. Builds the plugin (Release) into <GameDir>\BepInEx\plugins\FollowingSeasCoop.
    3. Packs dist\FollowingSeasCoop-<version>.zip:
         BepInEx\plugins\FollowingSeasCoop\FollowingSeasCoop.dll
         BepInEx\plugins\FollowingSeasCoop\LiteNetLib.dll
         BepInEx\plugins\FollowingSeasCoop\Facepunch.Steamworks.Win64.dll
         BepInEx\plugins\FollowingSeasCoop\steam_api64.dll
         BepInEx\plugins\FollowingSeasCoop\README.md
         BepInEx\plugins\FollowingSeasCoop\LICENSE
         BepInEx\plugins\FollowingSeasCoop\THIRD-PARTY-NOTICES.md

    The version is the one in Plugin.cs. The game must not be running from <GameDir>: the build
    cannot replace a library that is loaded.

.EXAMPLE
    Tools\Make-Release.ps1
    Tools\Make-Release.ps1 -GameDir 'D:\Games\Following Seas' -SkipSmoke
#>
param(
    [string]$GameDir = 'B:\FollowingSeasModTest',
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$source = Get-Content (Join-Path $root 'Plugin.cs') -Raw
if ($source -notmatch 'const string Version = "([^"]+)"') { Write-Host 'No version found in Plugin.cs'; exit 1 }
$version = $Matches[1]
Write-Host "Following Seas Co-op $version"

if (-not $SkipSmoke) {
    Write-Host 'Checking the wire layer...'
    dotnet run --project (Join-Path $root 'Tools\ProtocolSmoke\ProtocolSmoke.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { Write-Host 'ProtocolSmoke failed: nothing was packed.'; exit 1 }
}

Write-Host 'Building...'
dotnet build (Join-Path $root 'FollowingSeasCoop.csproj') -c Release "-p:GameDir=$GameDir" --nologo -v:q
if ($LASTEXITCODE -ne 0) { Write-Host 'The build failed (is the game running?): nothing was packed.'; exit 1 }

$plugin = Join-Path $GameDir 'BepInEx\plugins\FollowingSeasCoop'
$files = @(
    (Join-Path $plugin 'FollowingSeasCoop.dll'),
    (Join-Path $plugin 'LiteNetLib.dll'),
    (Join-Path $plugin 'Facepunch.Steamworks.Win64.dll'),
    (Join-Path $plugin 'steam_api64.dll'),
    (Join-Path $root 'README.md'),
    (Join-Path $root 'LICENSE'),
    (Join-Path $root 'THIRD-PARTY-NOTICES.md')
)
foreach ($file in $files) { if (-not (Test-Path $file)) { Write-Host "Missing: $file"; exit 1 } }

# The library that was built must be the version that is packed.
$built = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($files[0]).FileVersion
if ($built -and -not $built.StartsWith($version)) { Write-Host "The built library says $built, Plugin.cs says $version"; exit 1 }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$target = Join-Path $stage 'BepInEx\plugins\FollowingSeasCoop'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $files $target

$zip = Join-Path $dist "FollowingSeasCoop-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zip
Remove-Item -Recurse -Force $stage

Write-Host ''
Write-Host "Packed: $zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { Write-Host "SHA256: $($_.Hash)" }
