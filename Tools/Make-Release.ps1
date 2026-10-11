<#
.SYNOPSIS
    Builds the plugin and packs the two archives of a release into dist\.

.DESCRIPTION
    dist\FollowingSeasCoop-<version>.zip               the complete archive: the mod and the loader
                                                       (BepInEx) it needs. Unpacked into the game
                                                       folder, it is the whole installation.
    dist\FollowingSeasCoop-<version>-plugin-only.zip   the mod alone, for a game that already has
                                                       BepInEx 5 and for mod sites.

    Both hold BepInEx\plugins\FollowingSeasCoop\ with the four libraries, README.md, LICENSE and
    THIRD-PARTY-NOTICES.md. The complete one adds the pinned BepInEx archive as it is published
    and BepInEx\licenses\ (Tools\ReleaseNotices).

    1. Runs Tools/ProtocolSmoke: a release with a broken wire layer is not packed.
    2. Builds the plugin (Release) into a staging folder under dist\stage, so the game's own plugin
       folder is not touched and a running game does not lock the build. <GameDir> is only where
       the game's and the loader's libraries are read from.
    3. Checks the version of the built library against Plugin.cs, and that every file is there.
    4. Packs, and prints what is in each archive with its SHA256.

    The loader archive is kept in dist\cache and downloaded when it is not there; its SHA256 is
    checked either way.

.EXAMPLE
    Tools\Make-Release.ps1
    Tools\Make-Release.ps1 -GameDir 'D:\Games\Following Seas' -SkipSmoke
#>
param(
    [string]$GameDir = 'B:\FollowingSeasModTest',
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

# The loader the mod was written and run with. 5.4.23.5 is the first that writes its log on Unity 6.
$bepInExVersion = '5.4.23.5'
$bepInExSha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'

function Fail([string]$message) { Write-Host "Make-Release: $message"; exit 1 }

$source = Get-Content (Join-Path $root 'Plugin.cs') -Raw
if ($source -notmatch 'const string Version = "([^"]+)"') { Fail 'no version found in Plugin.cs' }
$version = $Matches[1]
Write-Host "Following Seas Co-op $version"

if (-not $SkipSmoke) {
    Write-Host 'Checking the wire layer...'
    dotnet run --project (Join-Path $root 'Tools\ProtocolSmoke\ProtocolSmoke.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { Fail 'ProtocolSmoke failed: nothing was packed.' }
}

$stage = Join-Path $dist 'stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$built = Join-Path $stage 'built'
New-Item -ItemType Directory -Force $built | Out-Null

Write-Host 'Building...'
dotnet build (Join-Path $root 'FollowingSeasCoop.csproj') -c Release "-p:GameDir=$GameDir" "-p:PluginDir=$built" --nologo -v:q
if ($LASTEXITCODE -ne 0) { Fail 'the build failed: nothing was packed.' }

$libraries = 'FollowingSeasCoop.dll', 'LiteNetLib.dll', 'Facepunch.Steamworks.Win64.dll', 'steam_api64.dll'
$documents = 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
$unexpected = @(Get-ChildItem $built -File | Where-Object { $libraries -notcontains $_.Name })
if ($unexpected.Count -gt 0) { Fail "the build left files that are not part of the mod: $($unexpected.Name -join ', ')" }
foreach ($file in $libraries) { if (-not (Test-Path (Join-Path $built $file))) { Fail "the build did not produce $file" } }
foreach ($file in $documents) { if (-not (Test-Path (Join-Path $root $file))) { Fail "$file is missing" } }

# The library that was built must be the version that is packed.
$builtVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $built 'FollowingSeasCoop.dll')).FileVersion
if (-not $builtVersion -or -not $builtVersion.StartsWith($version)) { Fail "the built library says '$builtVersion', Plugin.cs says $version" }

# The mod's own folder, the same in both archives.
function Add-Plugin([string]$target) {
    $folder = Join-Path $target 'BepInEx\plugins\FollowingSeasCoop'
    New-Item -ItemType Directory -Force $folder | Out-Null
    foreach ($file in $libraries) { Copy-Item (Join-Path $built $file) $folder }
    foreach ($file in $documents) { Copy-Item (Join-Path $root $file) $folder }
}

# The loader as its authors publish it, and the notices its licenses ask for.
function Add-Loader([string]$target) {
    $cache = Join-Path $dist 'cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $loaderZip = Join-Path $cache "BepInEx_win_x64_$bepInExVersion.zip"
    if (-not (Test-Path $loaderZip)) {
        Write-Host "Downloading BepInEx $bepInExVersion..."
        Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$bepInExVersion/BepInEx_win_x64_$bepInExVersion.zip" -OutFile $loaderZip
    }
    if ((Get-FileHash $loaderZip -Algorithm SHA256).Hash -ne $bepInExSha256) { Fail "$loaderZip is not the BepInEx archive that is expected (SHA256)" }
    Expand-Archive -LiteralPath $loaderZip -DestinationPath $target -Force
    foreach ($file in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx\core\BepInEx.dll', 'BepInEx\core\BepInEx.Preloader.dll', 'BepInEx\core\0Harmony.dll') {
        if (-not (Test-Path (Join-Path $target $file))) { Fail "the BepInEx archive has no $file" }
    }

    $notices = Join-Path $PSScriptRoot 'ReleaseNotices'
    foreach ($file in 'BepInEx-LICENSE.txt', 'Doorstop-LICENSE.txt', 'Harmony-LICENSE.txt', 'Cecil-LICENSE.txt', 'MonoMod-LICENSE.txt') {
        if (-not (Test-Path (Join-Path $notices $file))) { Fail "Tools\ReleaseNotices\$file is missing" }
    }
    # Doorstop is LGPL: the source beside the binary must be of the binary's version, and that
    # version is decided by the loader archive, not by this script.
    $doorstopVersion = (Get-Content (Join-Path $target '.doorstop_version') -Raw).Trim()
    $doorstopSource = "UnityDoorstop-$doorstopVersion-source.zip"
    if (-not (Test-Path (Join-Path $notices $doorstopSource))) { Fail "the loader carries Doorstop $doorstopVersion, but Tools\ReleaseNotices\$doorstopSource is missing (see SOURCES.md there)" }
    $others = @(Get-ChildItem $notices -Filter 'UnityDoorstop-*-source.zip' | Where-Object { $_.Name -ne $doorstopSource })
    if ($others.Count -gt 0) { Fail "Tools\ReleaseNotices holds Doorstop source of another version: $($others.Name -join ', ')" }

    $licenses = Join-Path $target 'BepInEx\licenses'
    New-Item -ItemType Directory -Force $licenses | Out-Null
    Get-ChildItem $notices -File | Where-Object { $_.Name -ne 'SOURCES.md' } | Copy-Item -Destination $licenses
}

function Pack([string]$folder, [string]$name) {
    $zip = Join-Path $dist $name
    if (Test-Path $zip) { Remove-Item -Force $zip }
    # CreateFromDirectory takes the loader's hidden .doorstop_version along; Compress-Archive does not.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip)
    Write-Host ''
    Write-Host ("$zip  " + [math]::Round((Get-Item $zip).Length / 1MB, 2) + ' MB')
    Write-Host ('SHA256 ' + (Get-FileHash $zip -Algorithm SHA256).Hash)
    Get-ChildItem $folder -Recurse -File -Force | ForEach-Object { Write-Host ('    ' + $_.FullName.Substring($folder.Length + 1)) }
}

$complete = Join-Path $stage 'complete'
Add-Loader $complete
Add-Plugin $complete
Pack $complete "FollowingSeasCoop-$version.zip"

$pluginOnly = Join-Path $stage 'plugin-only'
Add-Plugin $pluginOnly
Pack $pluginOnly "FollowingSeasCoop-$version-plugin-only.zip"

Remove-Item -Recurse -Force $stage
