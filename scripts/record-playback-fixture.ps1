#requires -Version 7.2
[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Output = '.crer/fixture.raw-input.ndjson',
  [string] $Scenario = '.crer/fixture.recorded.crer.yaml',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $CrerExe = 'dist/win-x64/crer.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Specify Chrome for Testing with -Chrome or CRER_CHROME.' }
$CrerExe = Resolve-FixturePath $CrerExe
$outputPath = Resolve-FixturePath $Output
$scenarioPath = Resolve-FixturePath $Scenario
$stopPath = "$outputPath.stop"
foreach ($file in @($outputPath, "$outputPath.meta.json", $scenarioPath, $stopPath)) {
  if (Test-Path -LiteralPath $file) { throw "Output already exists; select a new path to preserve recorded data: $file" }
}
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force
$server = Start-OwnedFixtureServer -Port $Port
$record = $null
try {
  $url = "http://127.0.0.1:$Port/index.html"
  Write-Host 'CfT のページ左上にある 64x64 のマゼンタ点を最初にクリックして較正してください。点が消えた後に query を入力し、Submit をクリックしてから、ここで Enter を押します。'
  $info = [Diagnostics.ProcessStartInfo]::new($CrerExe)
  $info.WorkingDirectory = $script:FixtureProjectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  foreach ($argument in @('record', $outputPath, '--url', $url, '--chrome', (Resolve-FixturePath $Chrome), '--stop-file', $stopPath)) { $info.ArgumentList.Add($argument) }
  $record = [Diagnostics.Process]::Start($info)
  $null = Read-Host '操作後、ここで Enter を押して記録を終了'
  $null = New-Item -ItemType File -Path $stopPath
  if (-not $record.WaitForExit(15000)) { throw 'Record did not stop within 15 seconds after its stop file.' }
  if ($record.ExitCode -ne 0) { throw "record exited with code $($record.ExitCode)" }
  & $CrerExe normalize $outputPath --url $url --output $scenarioPath --name fixture-recorded
  if ($LASTEXITCODE -ne 0) { throw "normalize exited with code $LASTEXITCODE" }
  & $CrerExe validate $scenarioPath
  if ($LASTEXITCODE -ne 0) { throw "validate exited with code $LASTEXITCODE" }
  Write-Host "Wrote and validated normalized scenario: $scenarioPath"
} finally {
  if ($record) {
    if (-not $record.HasExited) {
      if (-not (Test-Path -LiteralPath $stopPath)) { $null = New-Item -ItemType File -Path $stopPath }
      if (-not $record.WaitForExit(15000)) { $record.Kill($true); $null = $record.WaitForExit(5000) }
    }
    $record.Dispose()
  }
  if (Test-Path -LiteralPath $stopPath) { Remove-Item -LiteralPath $stopPath -Force }
  Stop-OwnedFixtureServer $server
}
