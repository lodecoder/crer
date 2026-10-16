#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
$root = Join-Path $script:FixtureProjectRoot ('.crer/fixture-input-tests/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$source = Join-Path $root 'source'
$null = New-Item -ItemType Directory -Path (Join-Path $source 'sub'), (Join-Path $source 'images')
$scenario = Join-Path $source 'sub/first.yaml'
$jsonScenario = Join-Path $source 'second.json'
$plan = Join-Path $source 'plan.yaml'
$asset = Join-Path $source 'images/pixel.png'
[IO.File]::WriteAllBytes($asset, [byte[]](1, 2, 3))
$yaml = @'
version: 1
name: copied-fixture
browser: { initial_url: 'http://127.0.0.1:8080/window?case=copy#fragment' }
functions:
  clickImage:
    params: [path]
    steps: [{ do: click, template: { path: '${path}', on_missing: skip } }]
steps:
  - { do: call, function: clickImage, args: { path: '../images/pixel.png' } }
  - { do: call, function: clickImage, args: { path: 'C:/absolute/image.png' } }
  - { do: click, template: { path: '../images/pixel.png', on_missing: skip } }
  - { do: navigate, url: 'https://external.example:8080/unchanged' }
'@
[IO.File]::WriteAllText($scenario, $yaml)
[IO.File]::WriteAllText($jsonScenario, '{"version":1,"name":"json-fixture","browser":{"initial_url":"http://localhost:8080/window"},"steps":[]}')
[IO.File]::WriteAllText($plan, @'
version: 1
name: copied-plan
run:
  serial:
    - scenario: sub/first.yaml
    - parallel:
        jobs:
          - scenario: sub/first.yaml
          - scenario: second.json
'@)
$hashes = @{}
foreach ($path in @($scenario, $jsonScenario, $plan, $asset)) { $hashes[$path] = (Get-FileHash -LiteralPath $path).Hash }
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
$prepared = $null
try {
  $default = New-FixtureInput -Path $scenario -Port 8080 -Evidence $root
  Check ($default.Path -ceq $scenario -and @($default.Files).Count -eq 0) '標準portでも元入力のコピーを要求した。'
  $prepared = New-FixtureInput -Path $plan -Port 18473 -Evidence $root -Command run
  Check (@($prepared.Files).Count -eq 3) 'planの参照先を一度ずつ再帰コピーできない。'
  foreach ($file in $prepared.Files) {
    Check ([IO.Path]::GetDirectoryName($file.Working) -ceq [IO.Path]::GetDirectoryName($file.Source)) '元入力とコピーの親が異なり相対パスの解決先が変わった。'
    Check ((Get-FileHash -LiteralPath $file.Working).Hash -ceq (Get-FileHash -LiteralPath $file.Evidence).Hash) '証跡のコピー内容が実行入力と一致しない。'
  }
  $scenarioCopy = @($prepared.Files | Where-Object Source -CEQ $scenario)[0]
  $jsonCopy = @($prepared.Files | Where-Object Source -CEQ $jsonScenario)[0]
  $scenarioText = [IO.File]::ReadAllText($scenarioCopy.Working)
  Check ($scenarioText.Contains('http://127.0.0.1:18473/window?case=copy#fragment')) 'YAMLのfixture URLにportが反映されない。'
  Check ($scenarioText.Contains("'`${path}'") -and $scenarioText.Contains('../images/pixel.png') -and $scenarioText.Contains('C:/absolute/image.png')) '相対templateや関数の絶対・相対パス引数を改変した。'
  Check ($scenarioText.Contains('https://external.example:8080/unchanged')) 'fixture以外のURLを改変した。'
  Check ([IO.File]::ReadAllText($jsonCopy.Working).Contains('http://localhost:18473/window')) 'JSON入力のfixture URLにportが反映されない。'
  $planText = [IO.File]::ReadAllText($prepared.Path)
  Check ([regex]::Matches($planText, [regex]::Escape([IO.Path]::GetFileName($scenarioCopy.Working))).Count -eq 2) '同じscenarioへの相対参照を同じコピーへ付け替えなかった。'
  Check (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($scenarioCopy.Working)) '../images/pixel.png')) 'コピーからのtemplate相対パスが元assetへ解決されない。'
} finally { Remove-FixtureInput $prepared }
foreach ($path in $hashes.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -ceq $hashes[$path]) "元入力を変更した: $path" }
Check (@(Get-ChildItem -LiteralPath $source -Recurse -Filter '.crer-fixture-*.yaml').Count -eq 0) '実行後に作業コピーが残っている。'

$failure = Join-Path $source 'invalid-plan.yaml'
[IO.File]::WriteAllText($failure, "version: 1`nname: invalid`nrun: { serial: [{ scenario: sub/first.yaml }, { scenario: missing.yaml }] }")
$failureEvidence = Join-Path $root 'failure'
$null = New-Item -ItemType Directory -Path $failureEvidence
$rejected = $false
try { $null = New-FixtureInput -Path $failure -Port 18473 -Evidence $failureEvidence -Command run }
catch { $rejected = $true }
Check $rejected '参照先不足を成功扱いにした。'
Check (@(Get-ChildItem -LiteralPath $source -Recurse -Filter '.crer-fixture-*.yaml').Count -eq 0) '準備の途中失敗で作業コピーが残った。'

if ($IsWindows) {
  $legacyShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
  foreach ($entry in @('test-release.ps1', 'test-playback-fixture.ps1', 'test-plan-fixture.ps1', 'test-session-reuse-fixture.ps1',
      'test-csharp-fixture.ps1', 'test-csharp-errors.ps1', 'test-csharp-record.ps1', 'test-csharp-manual-record.ps1',
      'record-playback-fixture.ps1', 'serve-playback-fixture.ps1', 'compat/Test-PlaybackParity.ps1')) {
    $parameters = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot $entry))
    if ($entry -eq 'test-csharp-errors.ps1') { $parameters += @('-CrerExe', 'unused.exe') }
    $output = & $legacyShell @parameters 2>&1 | Out-String
    $code = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $root ('powershell51-' + $entry.Replace('/', '-') + '.log')), $output)
    $requiredVersion = if ($entry -eq 'compat/Test-PlaybackParity.ps1') { '7\s*\.\s*4' } else { '7\s*\.\s*2' }
    Check ($code -ne 0 -and $output.Contains('ScriptRequiresUnmatchedPSVersion') -and $output -match $requiredVersion) "PowerShell 5.1をAPI実行前に拒否しなかった: $entry"
  }
}
[pscustomobject]@{ passed = $true; port = 18473; sourceFilesUnchanged = $true; copiesRemoved = $true; powershell = $PSVersionTable.PSVersion.ToString() } |
  ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'summary.json') -Encoding utf8
Write-Host "PASS fixture入力コピー・相対参照・port変更・PowerShell要件: $root"
