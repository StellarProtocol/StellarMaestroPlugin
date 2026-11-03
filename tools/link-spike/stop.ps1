<#
.SYNOPSIS
  Maestro-Link sync spike: tell every Maestro client to stop playback (new cmd id, op=stop).
#>
param([string] $Dir = 'C:\Users\Public\maestro-link-spike')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$cmdPath = Join-Path $Dir 'cmd.json'

$id = 1
if (Test-Path $cmdPath) {
    try { $id = [long]((Get-Content $cmdPath -Raw | ConvertFrom-Json).id) + 1 } catch { $id = 1 }
}
$json = [ordered]@{ id = $id; op = 'stop' } | ConvertTo-Json
$tmp = "$cmdPath.tmp"
[IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -Force $tmp $cmdPath
Write-Host "cmd id=$id stop"
