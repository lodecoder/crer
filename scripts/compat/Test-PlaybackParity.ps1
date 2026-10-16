#requires -Version 7.4
[CmdletBinding()]
param(
  [string] $CrerExe = 'dist/win-x64/crer.exe',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Scenario = 'fixtures/playback/click-only.crer.yaml',
  [ValidateSet('play', 'run')][string] $Command = 'play',
  [string] $Profile,
  [int] $ExpectedExitCode = 0,
  [int] $Port = 8080,
  [string] $Deno = 'deno'
)
# Optional historical comparison. The ordinary build and fixture scripts use C# only.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../fixture-common.ps1')
$legacy = & (Join-Path $PSScriptRoot 'Export-Legacy.ps1') -BuildNative
$scenarioPath = Resolve-FixturePath $Scenario
$scenarioHash = (Get-FileHash -LiteralPath $scenarioPath -Algorithm SHA256).Hash
$server = Start-OwnedFixtureServer -Port $Port
$inputCopy = $null
try {
  $inputCopy = New-FixtureInput -Path $scenarioPath -Port $Port -Evidence $server.Root -Command $Command
  $chromePath = Resolve-FixturePath $Chrome
  $legacyDll = Join-Path $legacy 'native/bin/Release/net10.0/win-x64/publish/crer-win-input.dll'
  $tail = @($Command, $inputCopy.Path, '--chrome', $chromePath)
  if ($Command -eq 'play') { $tail += @('--keep-artifacts', '--seed', '42') }
  if ($Profile) { $tail += @('--profile-dir', $Profile) }
  $current = Invoke-FixtureCli -Executable $CrerExe -Arguments $tail -ExpectedCode $ExpectedExitCode -Evidence $server.Root -Label 'csharp'
  $runtime = (Get-Command $Deno -CommandType Application -ErrorAction Stop).Source
  $oldArguments = @('run', '-A', '--config', (Join-Path $legacy 'deno.json'), (Join-Path $legacy 'src/main.ts')) + $tail + @('--dll', $legacyDll)
  $old = Invoke-FixtureCli -Executable $runtime -Arguments $oldArguments -ExpectedCode $ExpectedExitCode -Evidence $server.Root -Label 'legacy'
  if (($current.failures | ConvertTo-Json -Compress) -ne ($old.failures | ConvertTo-Json -Compress)) { throw 'Failure results differ; inspect captured stdout/stderr.' }
  $diagnosticDifferences = @()
  $comparedRuns = @()
  if (@($current).Count -ne @($old).Count) { throw 'Scenario result counts differ.' }
  for ($pairIndex = 0; $pairIndex -lt @($current).Count; $pairIndex++) {
    $currentResult = @($current)[$pairIndex]; $oldResult = @($old)[$pairIndex]
    if ($currentResult.code -ne $oldResult.code) { throw 'Scenario exit codes differ.' }
    $currentRun = Resolve-FixturePath $currentResult.runDir
    $oldRun = Resolve-FixturePath $oldResult.runDir
    $currentSteps = @(Get-Content -LiteralPath (Join-Path $currentRun 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
    $oldSteps = @(Get-Content -LiteralPath (Join-Path $oldRun 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
    if ($currentSteps.Count -ne $oldSteps.Count) { throw 'Step log counts differ.' }
    for ($index = 0; $index -lt $currentSteps.Count; $index++) {
    $left = $currentSteps[$index]; $right = $oldSteps[$index]
    foreach ($key in @('startedAt', 'completedAt')) { $null = $left.Remove($key); $null = $right.Remove($key) }
    # Baseline omitted successful log.message despite the documented artifact contract.
    # Preserve this diagnostic difference explicitly; do not compare away step behavior.
    if ($left.do -eq 'log' -and $left.status -eq 'ok' -and $left.ContainsKey('message') -and -not $right.ContainsKey('message')) {
      $diagnosticDifferences += [pscustomobject]@{ index = $left.index; field = 'message'; change = 'C# now retains the successful log message' }
      $null = $left.Remove('message')
    }
    $leftNode = [System.Text.Json.Nodes.JsonNode]::Parse(($left | ConvertTo-Json -Depth 50 -Compress))
    $rightNode = [System.Text.Json.Nodes.JsonNode]::Parse(($right | ConvertTo-Json -Depth 50 -Compress))
    if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($leftNode, $rightNode)) { throw "Step $index differs; inspect $currentRun and $oldRun. Only timestamps are normalized." }
    }
    $currentSeed = (Get-Content -Raw -LiteralPath (Join-Path $currentRun 'run.json') | ConvertFrom-Json).seed
    $oldSeed = (Get-Content -Raw -LiteralPath (Join-Path $oldRun 'run.json') | ConvertFrom-Json).seed
    if ($currentSeed -ne $oldSeed) { throw 'Replay seeds differ.' }
    $comparedRuns += [pscustomobject]@{ legacyRun = $oldRun; csharpRun = $currentRun; comparedSteps = $currentSteps.Count; seed = $currentSeed }
  }
  if ((Get-FileHash -LiteralPath $scenarioPath -Algorithm SHA256).Hash -ne $scenarioHash) { throw 'Comparison modified the scenario YAML.' }
  [pscustomobject]@{ baseline = '50b50fd8bf82a3ce09c25040bca51f60304ba126'; scenarioSha256 = $scenarioHash; code = $ExpectedExitCode;
    runs = $comparedRuns; diagnosticDifferences = $diagnosticDifferences } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $server.Root 'parity-summary.json')
  Write-Host "PASS fixed-ref playback parity; evidence: $($server.Root)"
} finally { try { Remove-FixtureInput $inputCopy } finally { Stop-OwnedFixtureServer $server } }
