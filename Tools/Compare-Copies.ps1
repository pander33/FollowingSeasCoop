<#
.SYNOPSIS
    Compares what two copies of a session hold of the shared world.

.DESCRIPTION
    The Debug panel's "Dump state" button (or Dev/DumpEverySeconds, which writes on every copy at
    the same moment of the host's clock) writes BepInEx/coop-state-<who>.txt. This script puts the
    host's file next to a guest's and prints only what differs:

      differs    the same thing reads differently on the two copies
      only ...   one copy has a thing the other does not

    Numbers inside a value may differ by the line's own tolerance (a settled barrel by 0.1 m, the
    hull's water by 100 kg). Lines the mod marks as a copy's own are not compared.

    Exit code 0 when the copies agree, 1 when they do not.

.EXAMPLE
    Tools\Compare-Copies.ps1
    Tools\Compare-Copies.ps1 -Guest guest3 -Section Controls
    Tools\Compare-Copies.ps1 -Watch
#>
param(
    [string]$Dir = 'B:\FollowingSeasModTest\BepInEx',
    [string]$HostName = 'host',
    # Which guest; by default the first coop-state-guest*.txt found.
    [string]$Guest = '',
    [string]$Section = '',
    # Multiplies every line's tolerance.
    [double]$Tolerance = 1,
    # Also print the lines that are a copy's own.
    [switch]$Info,
    # Compare again every time the files change (use with Dev/DumpEverySeconds).
    [switch]$Watch
)

$number = [regex]'-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Read-State([string]$path) {
    $state = @{ Header = ''; Tick = 0L; Lines = [ordered]@{} }
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line.StartsWith('#')) {
            $state.Header = $line.Substring(1).Trim()
            $m = [regex]::Match($line, 'host clock (\d+) ms')
            if ($m.Success) { $state.Tick = [long]$m.Groups[1].Value }
            continue
        }
        $parts = $line.Split("`t")
        if ($parts.Length -lt 4) { continue }
        $state.Lines[$parts[0] + ' | ' + $parts[1]] = @{ Section = $parts[0]; Tol = $parts[2]; Value = $parts[3] }
    }
    return $state
}

function Test-Same([string]$a, [string]$b, [double]$tol) {
    if ($a -eq $b) { return $true }
    # The same text around the numbers, and every number within the tolerance.
    if ($number.Replace($a, '#') -ne $number.Replace($b, '#')) { return $false }
    $x = $number.Matches($a); $y = $number.Matches($b)
    for ($i = 0; $i -lt $x.Count; $i++) {
        $p = [double]::Parse($x[$i].Value, $invariant); $q = [double]::Parse($y[$i].Value, $invariant)
        if ([math]::Abs($p - $q) -gt $tol) { return $false }
    }
    return $true
}

function Compare-Once {
    $hostPath = Join-Path $Dir "coop-state-$HostName.txt"
    if ($Guest) { $guestPath = Join-Path $Dir "coop-state-$Guest.txt" }
    else {
        $found = Get-ChildItem -Path $Dir -Filter 'coop-state-guest*.txt' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $guestPath = $found.FullName } else { $guestPath = '' }
    }
    if (-not (Test-Path $hostPath) -or -not $guestPath -or -not (Test-Path $guestPath)) {
        Write-Host "Need coop-state-$HostName.txt and a coop-state-guest*.txt in $Dir. Press 'Dump state' in the Debug panel of both copies."
        return 2
    }
    $h = Read-State $hostPath
    $g = Read-State $guestPath
    Write-Host "host : $($h.Header)"
    Write-Host "guest: $($g.Header)"
    $apart = [math]::Abs($h.Tick - $g.Tick)
    if ($apart -gt 1500) {
        Write-Host ("NOTE: the two files were written {0:F1} s apart; whatever was changing then will differ." -f ($apart / 1000)) -ForegroundColor Yellow
    }

    $differences = 0; $compared = 0
    $keys = New-Object System.Collections.Generic.List[string]
    foreach ($k in $h.Lines.Keys) { $keys.Add($k) }
    foreach ($k in $g.Lines.Keys) { if (-not $h.Lines.Contains($k)) { $keys.Add($k) } }
    foreach ($k in $keys) {
        $a = $h.Lines[$k]; $b = $g.Lines[$k]
        $one = if ($a) { $a } else { $b }
        if ($Section -and $one.Section -ne $Section) { continue }
        if ($one.Tol -eq '-') {
            if ($Info) {
                $av = if ($a) { $a.Value } else { '(none)' }; $bv = if ($b) { $b.Value } else { '(none)' }
                Write-Host "  own      $k" -ForegroundColor DarkGray
                Write-Host "             host : $av" -ForegroundColor DarkGray
                Write-Host "             guest: $bv" -ForegroundColor DarkGray
            }
            continue
        }
        $compared++
        if (-not $a) { $differences++; Write-Host "  only guest  $k = $($b.Value)" -ForegroundColor Red; continue }
        if (-not $b) { $differences++; Write-Host "  only host   $k = $($a.Value)" -ForegroundColor Red; continue }
        $tol = [double]::Parse($a.Tol, $invariant) * $Tolerance
        if (-not (Test-Same $a.Value $b.Value $tol)) {
            $differences++
            Write-Host "  differs     $k" -ForegroundColor Red
            Write-Host "                host : $($a.Value)"
            Write-Host "                guest: $($b.Value)"
        }
    }
    if ($differences -eq 0) { Write-Host "The copies agree ($compared lines compared)." -ForegroundColor Green; return 0 }
    Write-Host "$differences difference(s) in $compared lines." -ForegroundColor Red
    return 1
}

if (-not $Watch) { exit (Compare-Once) }

$last = ''
while ($true) {
    $stamp = (Get-ChildItem -Path $Dir -Filter 'coop-state-*.txt' -ErrorAction SilentlyContinue |
              ForEach-Object { $_.LastWriteTimeUtc.Ticks }) -join ','
    if ($stamp -ne $last) {
        $last = $stamp
        # Both copies write at the same moment: give the second one time to finish.
        Start-Sleep -Milliseconds 400
        Clear-Host
        [void](Compare-Once)
        Write-Host ''
        Write-Host (Get-Date -Format 'HH:mm:ss') '- waiting for the next dump (Ctrl+C to stop)'
    }
    Start-Sleep -Milliseconds 500
}
