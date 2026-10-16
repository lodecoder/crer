#requires -Version 7.2
[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Plan = 'fixtures/playback/session-reuse.crer.plan.yaml',
  [string] $Profile,
  [string] $CrerExe = 'dist/win-x64/crer.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Specify Chrome for Testing with -Chrome or CRER_CHROME.' }
if ([string]::IsNullOrWhiteSpace($Profile)) { $Profile = '.crer/profiles/session-reuse-fixture-' + [Guid]::NewGuid().ToString('N') }
$server = Start-OwnedFixtureServer -Port $Port
$inputCopy = $null
try {
  $inputCopy = New-FixtureInput -Path $Plan -Port $Port -Evidence $server.Root -Command run
  $results = Invoke-FixtureCli -Executable $CrerExe -Arguments @('run', $inputCopy.Path, '--chrome', (Resolve-FixturePath $Chrome), '--profile-dir', $Profile) -ExpectedCode 0 -Evidence $server.Root -Label 'session-reuse'
  if (@($results).Count -ne 2) { throw 'Reuse plan must produce exactly two scenario results.' }
  $metadata = @($results | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path (Resolve-FixturePath $_.runDir) 'run.json') | ConvertFrom-Json })
  if (@($metadata | Where-Object { $_.browserSession.reused -eq $false }).Count -ne 1 -or @($metadata | Where-Object { $_.browserSession.reused -eq $true }).Count -ne 1) { throw 'Expected one new and one reused browser session.' }
  if ($metadata[0].chromePid -ne $metadata[1].chromePid) { throw 'Reuse plan created different Chrome processes.' }
  $last = Resolve-FixturePath $results[-1].runDir
  $shutdown = Join-Path $last 'shutdown.json'
  if (-not (Test-Path -LiteralPath $shutdown)) { throw 'Final session shutdown artifact is missing.' }
  $cleanup = Get-Content -Raw -LiteralPath $shutdown | ConvertFrom-Json
  if (-not $cleanup.chromeExited -or $cleanup.pendingCdpRequests -ne 0) { throw 'Reused browser did not shut down cleanly.' }
  Write-Host "PASS one CfT process reused then closed; profile retained at $Profile; harness: $($server.Root)"
} finally { try { Remove-FixtureInput $inputCopy } finally { Stop-OwnedFixtureServer $server } }
