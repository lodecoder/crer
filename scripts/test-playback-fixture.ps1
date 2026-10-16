#requires -Version 7.2
[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Scenario = 'fixtures/playback/search.crer.yaml',
  [int] $ExpectedExitCode = 0,
  [string] $CrerExe = 'dist/win-x64/crer.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Specify Chrome for Testing with -Chrome or CRER_CHROME.' }
if ([string]::IsNullOrWhiteSpace($CrerExe)) { $CrerExe = 'dist/win-x64/crer.exe' }
Add-Type -AssemblyName System.Windows.Forms
$server = Start-OwnedFixtureServer -Port $Port
$inputCopy = $null
try {
  $inputCopy = New-FixtureInput -Path $Scenario -Port $Port -Evidence $server.Root
  $before = [System.Windows.Forms.Cursor]::Position
  Start-Sleep -Milliseconds 1000
  $control = [System.Windows.Forms.Cursor]::Position
  $result = Invoke-FixtureCli -Executable $CrerExe -Arguments @('play', $inputCopy.Path, '--chrome', (Resolve-FixturePath $Chrome), '--keep-artifacts') -ExpectedCode $ExpectedExitCode -Evidence $server.Root -Label 'play'
  $run = Resolve-FixturePath $result.runDir
  foreach ($artifact in @('run.json', 'steps.ndjson', 'display.json', 'foreground.json', 'cleanup.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $run $artifact) -PathType Leaf)) { throw "Missing artifact: $artifact" }
  }
  $image = if ($ExpectedExitCode -eq 0) { 'result.png' } else { 'failure-0.png' }
  if (-not (Test-Path -LiteralPath (Join-Path $run $image))) { throw "Missing screenshot: $image" }
  $cleanup = Get-Content -Raw -LiteralPath (Join-Path $run 'cleanup.json') | ConvertFrom-Json
  if (-not $cleanup.chromeExited -or $cleanup.pendingCdpRequests -ne 0 -or $cleanup.errors.Count -ne 0) { throw 'Chrome/CDP cleanup failed.' }
  $foreground = Get-Content -Raw -LiteralPath (Join-Path $run 'foreground.json') | ConvertFrom-Json
  if ($foreground.captureError -or $foreground.restoreStatus -ne 0) { throw 'Foreground capture/restore failed.' }
  $after = [System.Windows.Forms.Cursor]::Position
  if ($before -ne $control -or $control -ne $after) { Write-Warning 'INCONCLUSIVE: physical cursor moved; external input cannot be isolated.' }
  else { Write-Host "PASS observed no physical cursor change ($before)." }
  Write-Host "PASS playback code $ExpectedExitCode; artifacts: $run; harness: $($server.Root)"
} finally { try { Remove-FixtureInput $inputCopy } finally { Stop-OwnedFixtureServer $server } }
