[CmdletBinding()]
param([string] $CrerExe = 'dist/csharp/win-x64/crer.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathRooted($CrerExe)) { $CrerExe = Join-Path $projectRoot $CrerExe }
$testRoot = Join-Path $projectRoot ('.crer/csharp-yaml-parity/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$cases = [Collections.Generic.List[object]]::new()
$prefix = "version: 1`nname: Test`nbrowser: { initial_url: 'http://127.0.0.1:8080/' }`n"
foreach ($scalar in @('_42', '1__2', '42_', 'yes', 'on', '0x2a', '0o52', '012', '1:20', '12e3', '1e+3', '+42', '.1', '1.', '1_2', '12_34', '0b10', '+0x10', '1e2_3', '-.1', '2026-10-15', '2026-10-15T01:02:03Z', "'2026-10-15'")) {
  $cases.Add(@{ name = "name-$scalar"; yaml = ($prefix.Replace('name: Test', "name: $scalar") + 'steps: []') })
}
foreach ($scalar in @('012', '0b10', '0x10', '0o10', '1_2', '42_', '_42', '1__2', '1e2_3', '!!int "1"', '!!int "0x1"', '!!float "1"')) {
  $cases.Add(@{ name = "coordinate-$scalar"; yaml = ($prefix + "steps: [{ do: click, at: { x: $scalar, y: 0 } }]") })
}
$cases.Add(@{ name = 'merge-overrides'; yaml = ($prefix + "steps:`n  - &base { do: click, at: { x: 1, y: 2 } }`n  - { <<: *base, at: { x: 3, y: 4 } }") })
$summary = [Collections.Generic.List[object]]::new()
Push-Location $projectRoot
try {
  $index = 0
  foreach ($case in $cases) {
    $file = Join-Path $testRoot "$index.crer.yaml"
    [IO.File]::WriteAllText($file, $case.yaml + "`n")
    $denoOutput = (& deno run -A src/main.ts validate $file 2>&1 | Out-String)
    $denoCode = $LASTEXITCODE
    $csharpOutput = (& $CrerExe validate $file 2>&1 | Out-String)
    $csharpCode = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $testRoot "$index.deno.log"), $denoOutput)
    [IO.File]::WriteAllText((Join-Path $testRoot "$index.csharp.log"), $csharpOutput)
    $summary.Add([pscustomobject]@{ name = $case.name; deno = $denoCode; csharp = $csharpCode })
    if ($denoCode -notin @(0, 2) -or $denoCode -ne $csharpCode) {
      throw "YAML parity failed ($($case.name)): Deno=$denoCode C#=$csharpCode; inspect $testRoot"
    }
    $index++
  }
  Write-Host "PASS $($cases.Count) YAML parity cases; evidence: $testRoot"
} finally {
  Pop-Location
  $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'summary.json')
}
