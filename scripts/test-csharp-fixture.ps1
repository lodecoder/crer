#requires -Version 7.2
[CmdletBinding()]
param(
  [string] $CrerExe = 'dist/win-x64/crer.exe',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $FixtureServerExe,
  [int] $Port = 8080
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Specify Chrome for Testing with -Chrome or CRER_CHROME.' }
$scenario = Resolve-FixturePath 'fixtures/playback/click-only.crer.yaml'
$hash = (Get-FileHash -LiteralPath $scenario -Algorithm SHA256).Hash
$server = Start-OwnedFixtureServer -Port $Port -Executable $FixtureServerExe
$inputCopy = $null
try {
  $inputCopy = New-FixtureInput -Path $scenario -Port $Port -Evidence $server.Root
  $summary = @()
  foreach ($keep in @($true, $false)) {
    $label = if ($keep) { 'flag-keep' } else { 'environment-dispose' }
    $arguments = @('play', $inputCopy.Path)
    if ($keep) { $arguments += @('--chrome', (Resolve-FixturePath $Chrome), '--keep-artifacts') }
    $environment = @{ CRER_CHROME = $(if ($keep) { Join-Path $server.Root 'missing.exe' } else { Resolve-FixturePath $Chrome }) }
    $result = Invoke-FixtureCli -Executable $CrerExe -Arguments $arguments -Environment $environment -ExpectedCode 0 -Evidence $server.Root -Label $label
    $run = Resolve-FixturePath $result.runDir
    foreach ($artifact in @('run.json', 'steps.ndjson', 'display.json', 'before.png', 'result.png', 'cleanup.json')) {
      if (-not (Test-Path -LiteralPath (Join-Path $run $artifact))) { throw "$label missing $artifact" }
    }
    foreach ($name in @('before.png', 'result.png')) {
      $bytes = [IO.File]::ReadAllBytes((Join-Path $run $name))
      if ($bytes.Length -le 8 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A') { throw "$label invalid PNG $name" }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $run 'before.png')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $run 'result.png')).Hash) { throw "$label screenshots show no page change." }
    $steps = @(Get-Content -LiteralPath (Join-Path $run 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
    if ($steps.Count -ne 7 -or @($steps | Where-Object { $_.status -ne 'ok' }).Count -ne 0 -or @($steps | Where-Object { $_.do -eq 'assert' }).Count -ne 2) { throw "$label fixture assertions failed." }
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $run 'run.json') | ConvertFrom-Json
    $cleanup = Get-Content -Raw -LiteralPath (Join-Path $run 'cleanup.json') | ConvertFrom-Json
    if ((Resolve-FixturePath $metadata.chrome) -ne (Resolve-FixturePath $Chrome)) { throw "$label wrong Chrome selection." }
    if (-not $cleanup.chromeExited -or $cleanup.pendingCdpRequests -ne 0 -or $cleanup.errors.Count -ne 0 -or $cleanup.profileRetained -ne $keep) { throw "$label cleanup failed." }
    if ($null -ne (Get-Process -Id $metadata.chromePid -ErrorAction SilentlyContinue)) { throw "$label own Chrome is still running." }
    $summary += [pscustomobject]@{ name = $label; code = 0; runDir = $run; scenarioSha256 = $hash; keepArtifacts = $keep }
    Write-Host "PASS $label assertions, PNGs and cleanup; artifacts: $run"
  }
  if ((Get-FileHash -LiteralPath $scenario -Algorithm SHA256).Hash -ne $hash) { throw 'Fixture YAML changed during acceptance.' }
  $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $server.Root 'summary.json')
} finally { try { Remove-FixtureInput $inputCopy } finally { Stop-OwnedFixtureServer $server; Write-Host "Fixture evidence: $($server.Root)" } }
