[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Scenario = 'fixtures\\playback\\search.crer.yaml',
  [int] $ExpectedExitCode = 0
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}
Add-Type -AssemblyName System.Windows.Forms
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
  $before = [System.Windows.Forms.Cursor]::Position
  Start-Sleep -Seconds 1
  $control = [System.Windows.Forms.Cursor]::Position
  Push-Location $projectRoot
  try { & deno task dev play $Scenario --chrome $Chrome --keep-artifacts } finally { Pop-Location }
  if ($LASTEXITCODE -ne $ExpectedExitCode) { throw "play exited with code $LASTEXITCODE (expected $ExpectedExitCode)" }
  $run = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.crer\runs') -Directory |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if (-not $run -or -not (Test-Path -LiteralPath (Join-Path $run.FullName 'run.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $run.FullName 'result.png')) -or
    -not (Test-Path -LiteralPath (Join-Path $run.FullName 'display.json')) -or
    ($ExpectedExitCode -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $run.FullName 'failure-0.png')))) {
    throw 'Playback artifacts run.json, result.png, and display.json were not all created.'
  }
  Write-Host "Artifacts: $($run.FullName)"
  $after = [System.Windows.Forms.Cursor]::Position
  if ($before -ne $control) {
    Write-Warning "INCONCLUSIVE: cursor changed without playback ($before to $control); playback cannot be isolated in this desktop session."
  } elseif ($control -ne $after) {
    throw "Physical cursor changed during playback from $control to $after"
  } else {
    Write-Host "PASS: playback completed without changing the physical cursor ($before)."
  }
} finally {
  if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
