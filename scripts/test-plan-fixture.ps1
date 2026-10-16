[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Plan = 'fixtures/playback/continue-after-failure.crer.plan.yaml',
  [int] $ExpectedExitCode = 4,
  [string] $CrerExe = 'dist/win-x64/crer.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Specify Chrome for Testing with -Chrome or CRER_CHROME.' }
$server = Start-OwnedFixtureServer -Port $Port
try {
  $results = Invoke-FixtureCli -Executable $CrerExe -Arguments @('run', (Resolve-FixturePath $Plan), '--chrome', (Resolve-FixturePath $Chrome)) -ExpectedCode $ExpectedExitCode -Evidence $server.Root -Label 'plan'
  if (@($results).Count -ne 2) { throw 'Continue plan must produce exactly two scenario results.' }
  $failure = @($results | Where-Object { $_.code -eq 4 -and (Test-Path -LiteralPath (Join-Path (Resolve-FixturePath $_.runDir) 'failure-0.png')) })
  $success = @($results | Where-Object { $_.code -eq 0 -and (Test-Path -LiteralPath (Join-Path (Resolve-FixturePath $_.runDir) 'result.png')) })
  if ($failure.Count -ne 1 -or $success.Count -ne 1) { throw 'Plan did not preserve failure and successful artifacts.' }
  foreach ($result in $results) {
    $cleanup = Get-Content -Raw -LiteralPath (Join-Path (Resolve-FixturePath $result.runDir) 'cleanup.json') | ConvertFrom-Json
    if (-not $cleanup.chromeExited -or $cleanup.pendingCdpRequests -ne 0 -or $cleanup.errors.Count -ne 0) { throw 'Plan left Chrome/CDP resources.' }
  }
  Write-Host "PASS plan continued after scenario failure; harness: $($server.Root)"
} finally { Stop-OwnedFixtureServer $server }