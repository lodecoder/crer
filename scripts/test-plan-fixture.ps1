[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Plan = 'fixtures\playback\continue-after-failure.crer.plan.yaml',
  [int] $ExpectedExitCode = 4
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}
$server = Start-Process -FilePath (Get-Command pwsh -ErrorAction Stop).Source -ArgumentList @(
  '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'serve-playback-fixture.ps1'), '-Port', $Port
) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
try {
  $url = "http://127.0.0.1:$Port/index.html"
  $deadline = [DateTime]::UtcNow.AddSeconds(5)
  do {
    try { $ready = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 1 $url).StatusCode -eq 200 } catch { $ready = $false; Start-Sleep -Milliseconds 100 }
  } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
  if (-not $ready) { throw "Playback fixture did not start at $url" }
  Push-Location $projectRoot
  try { & deno task dev run $Plan --chrome $Chrome } finally { Pop-Location }
  if ($LASTEXITCODE -ne $ExpectedExitCode) { throw "run exited with code $LASTEXITCODE (expected $ExpectedExitCode)" }
  $runs = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.crer\runs') -Directory |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 2
  if ($runs.Count -ne 2) { throw 'Plan did not create two scenario run directories.' }
  $failure = $runs | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'failure-0.png') -PathType Leaf }
  $success = $runs | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'result.png') -PathType Leaf }
  if (-not $failure -or -not $success) { throw 'Plan did not retain both failure and successful playback artifacts.' }
  Write-Host "PASS: plan continued after scenario failure; artifacts: $($failure.FullName), $($success.FullName)"
} finally {
  if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
