<#
.SYNOPSIS
  Maestro-Link sync spike: tell every Maestro client to start playing at one shared server-time instant T.

.EXAMPLE
  .\start.ps1 -DelaySec 8 -Parts @{ 'Alice' = 'C:\spike\click_a.mid'; 'Bob' = 'C:\spike\click_b.mid' }
  .\start.ps1 -Default 'C:\spike\click_a.mid'

.NOTES
  T = UtcNow(ms) + DelaySec*1000. The game's server clock is ~Unix ms; if fresh status-*.json files exist (written 1 Hz
  by each client), the mean (serverNowMs - utcNowMs) offset is applied so T is expressed in SERVER time. Pass
  -NoClockCorrect to skip that. Each client refuses a T closer than its lookahead + 300 ms, so keep DelaySec >= 3.
  Clients act once per new id; the id is incremented from the existing cmd.json.
#>
param(
    [int]       $DelaySec = 8,
    [hashtable] $Parts    = @{},
    [string]    $Default  = $null,
    [string]    $Dir      = 'C:\Users\Public\maestro-link-spike',
    [switch]    $NoClockCorrect
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$cmdPath = Join-Path $Dir 'cmd.json'

$id = 1
if (Test-Path $cmdPath) {
    try { $id = [long]((Get-Content $cmdPath -Raw | ConvertFrom-Json).id) + 1 } catch { $id = 1 }
}

$utc = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$offset = 0
if (-not $NoClockCorrect) {
    $offs = @()
    foreach ($f in Get-ChildItem $Dir -Filter 'status-*.json' -ErrorAction SilentlyContinue) {
        if (((Get-Date) - $f.LastWriteTime).TotalSeconds -gt 5) { continue }   # stale client
        try {
            $s = Get-Content $f.FullName -Raw | ConvertFrom-Json
            if ([long]$s.serverNowMs -gt 0) { $offs += ([long]$s.serverNowMs - [long]$s.utcNowMs) }
        } catch { }
    }
    if ($offs.Count -gt 0) {
        $offset = [long](($offs | Measure-Object -Average).Average)
        Write-Host ("clock offsets server-utc (ms): {0}  -> using {1}" -f ($offs -join ', '), $offset)
    } else {
        Write-Host 'no fresh status files — assuming server clock == UTC'
    }
}
$T = $utc + $offset + [long]$DelaySec * 1000

$cmd = [ordered]@{ id = $id; op = 'play'; startAtServerMs = $T; parts = $Parts }
if ($Default) { $cmd.default = $Default }
$json = $cmd | ConvertTo-Json -Depth 4

# Write to a temp file, then replace — a polling client never reads a half-written command. UTF-8 without BOM.
$tmp = "$cmdPath.tmp"
[IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -Force $tmp $cmdPath
Write-Host "cmd id=$id play T=$T (in ${DelaySec}s)"
Write-Host $json
