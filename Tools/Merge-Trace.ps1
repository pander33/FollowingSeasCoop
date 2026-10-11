<#
.SYNOPSIS
    Reads the trace files of every copy of a session as one story.

.DESCRIPTION
    With Dev/Trace on, each copy writes BepInEx/coop-trace-<who>.log during a session. Every line
    carries the host's clock, so the lines of all copies can be put in one order: what the guest
    sent, when the host got it, what the host sent back, what the guest did with it.

    A guest only estimates the host's clock (a few milliseconds off), so two lines closer than
    that may stand in the wrong order.

.EXAMPLE
    Tools\Merge-Trace.ps1
    Tools\Merge-Trace.ps1 -Area Controls -Key 'Leak#2'
    Tools\Merge-Trace.ps1 -LastSeconds 30 -Out trace.txt
#>
param(
    [string]$Dir = 'B:\FollowingSeasModTest\BepInEx',
    # Only this subsystem: Controls, Items, Household, Tasks, Cargo, Hunt, World, Game, Coop.
    [string]$Area = '',
    # Only lines whose key or value holds this text.
    [string]$Key = '',
    # Only the end of the session.
    [double]$LastSeconds = 0,
    # Only lines that are a problem (warnings and errors of the mod, exceptions of the game).
    [switch]$Problems,
    [string]$Out = ''
)

$files = Get-ChildItem -Path $Dir -Filter 'coop-trace-*.log' -ErrorAction SilentlyContinue
if (-not $files) {
    Write-Host "No coop-trace-*.log in $Dir. Set Dev/Trace = true (or start with --coop:Dev.Trace=true) and play a session."
    exit 1
}

$rows = New-Object System.Collections.Generic.List[object]
$order = 0
foreach ($file in $files) {
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        if ($line.StartsWith('#') -or $line.Length -eq 0) { continue }
        $parts = $line.Split("`t")
        if ($parts.Length -lt 6) { continue }
        $tick = 0L
        if (-not [long]::TryParse($parts[0], [ref]$tick)) { continue }
        $order++
        $rows.Add([pscustomobject]@{
            Tick = $tick; Order = $order; Who = $parts[1]; Area = $parts[2]
            What = $parts[3]; Key = $parts[4]; Value = $parts[5]
        })
    }
}
if ($rows.Count -eq 0) { Write-Host 'The trace files hold no lines.'; exit 1 }

$sorted = $rows | Sort-Object Tick, Order
$start = $sorted[0].Tick
$end = $sorted[$sorted.Count - 1].Tick

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Trace of $($files.Count) copy(ies): $($files.Name -join ', ')")
$lines.Add('time since the first line | who | subsystem | what | key | value')
foreach ($row in $sorted) {
    if ($Area -and $row.Area -ne $Area) { continue }
    if ($Key -and $row.Key.IndexOf($Key, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        $row.Value.IndexOf($Key, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
    if ($LastSeconds -gt 0 -and ($end - $row.Tick) -gt $LastSeconds * 1000) { continue }
    if ($Problems -and $row.What -ne 'PROBLEM' -and $row.What -ne 'exception') { continue }
    $ms = $row.Tick - $start
    $time = '{0,3}:{1:00}.{2:000}' -f [math]::Floor($ms / 60000), ([math]::Floor($ms / 1000) % 60), ($ms % 1000)
    $lines.Add(('{0}  {1,-7} {2,-10} {3,-22} {4}  {5}' -f $time, $row.Who, $row.Area, $row.What, $row.Key, $row.Value))
}

if ($Out) {
    [System.IO.File]::WriteAllLines($Out, $lines)
    Write-Host "$($lines.Count - 2) lines written to $Out"
} else {
    $lines | ForEach-Object { Write-Host $_ }
}
