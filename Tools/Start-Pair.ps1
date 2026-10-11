<#
.SYNOPSIS
    Starts two copies of the game side by side, a host and a guest, set up for a test session.

.DESCRIPTION
    Both copies run from one folder and read one config file, so each is told who it is on its
    command line (--coop:Section.Key=value, read by the plugin and never written to the file):

      host   Dev/AutoHost: hosts by itself as soon as you have loaded a world.
      guest  its own save folder, its own name, Dev/AutoJoin: joins from the title screen and
             keeps trying until the host answers.

    Load your world in the left window; the right one joins by itself.
    The first copy writes BepInEx/LogOutput.log, the second LogOutput.log.1.

.EXAMPLE
    Tools\Start-Pair.ps1
    Tools\Start-Pair.ps1 -Trace -DumpEverySeconds 5 -LagMs 80
    Tools\Start-Pair.ps1 -NoAuto -NoTools
#>
param(
    [string]$GameDir = 'B:\FollowingSeasModTest',
    [string]$GuestSaveDir = '',
    [string]$GuestName = 'Guest',
    [int]$Width = 1280,
    [int]$Height = 720,
    # Each copy holds what arrives this long before acting on it (so one way costs this much).
    [int]$LagMs = 0,
    [int]$JitterMs = 0,
    # Write coop-trace-<who>.log (read with Merge-Trace.ps1).
    [switch]$Trace,
    # Write coop-state-<who>.txt this often (read with Compare-Copies.ps1 -Watch).
    [int]$DumpEverySeconds = 0,
    # Do not host and join by themselves.
    [switch]$NoAuto,
    # Do not show the Dev section of the menu.
    [switch]$NoTools,
    [switch]$HostOnly
)

$exe = Join-Path $GameDir 'Following Seas.exe'
if (-not (Test-Path $exe)) { Write-Host "No game at $exe"; exit 1 }
if (-not $GuestSaveDir) { $GuestSaveDir = Join-Path $GameDir '_guest-saves' }

Add-Type -Namespace Win32 -Name Window -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr handle, int x, int y, int width, int height, bool repaint);
'@

function Get-Common {
    $list = @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height",
              '--coop:Dev.RunInBackground=true', "--coop:Dev.LagMs=$LagMs", "--coop:Dev.JitterMs=$JitterMs",
              "--coop:Dev.DumpEverySeconds=$DumpEverySeconds")
    if ($Trace) { $list += '--coop:Dev.Trace=true' }
    if (-not $NoTools) { $list += '--coop:Dev.Tools=true' }
    return $list
}

function Start-Copy([string[]]$arguments, [int]$x) {
    # An argument with a space in it (a folder) must reach the game as one argument.
    $line = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $process = Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $line -PassThru
    for ($i = 0; $i -lt 120 -and $process.MainWindowHandle -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        [void][Win32.Window]::MoveWindow($process.MainWindowHandle, $x, 0, $Width, $Height + 40, $true)
    }
    return $process
}

$hostArgs = Get-Common
if (-not $NoAuto) { $hostArgs += '--coop:Dev.AutoHost=true' }
Write-Host 'Starting the host...'
$hostProcess = Start-Copy $hostArgs 0
Write-Host "  pid $($hostProcess.Id): load your world in this window."
if ($HostOnly) { exit 0 }

# The first copy takes LogOutput.log; the second then writes LogOutput.log.1.
Start-Sleep -Seconds 3
$guestArgs = Get-Common
$guestArgs += "--coop:Dev.SaveDir=$GuestSaveDir"
$guestArgs += "--coop:Player.Name=$GuestName"
$guestArgs += '--coop:Network.JoinIp=127.0.0.1'
if (-not $NoAuto) { $guestArgs += '--coop:Dev.AutoJoin=true' }
Write-Host 'Starting the guest...'
$guestProcess = Start-Copy $guestArgs $Width
Write-Host "  pid $($guestProcess.Id): saves in $GuestSaveDir."
Write-Host ''
Write-Host 'Logs:   ' (Join-Path $GameDir 'BepInEx\LogOutput.log') ' (host),  LogOutput.log.1 (guest)'
if ($Trace) { Write-Host 'Trace:   Tools\Merge-Trace.ps1' }
if ($DumpEverySeconds -gt 0) { Write-Host 'Compare: Tools\Compare-Copies.ps1 -Watch' }
