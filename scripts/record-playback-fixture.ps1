[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Output = '.crer\\fixture.raw-input.ndjson',
  [string] $Scenario = '.crer\\fixture.recorded.crer.yaml',
  [string] $Chrome = $env:CRER_CHROME
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureUrl = "http://127.0.0.1:$Port/index.html"

if (-not (Get-Command deno -ErrorAction SilentlyContinue)) {
  throw 'deno was not found.'
}
if ([string]::IsNullOrWhiteSpace($Chrome)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}
if (-not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw "Chrome executable was not found: $Chrome"
}

$outputPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
$scenarioPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $Scenario))
$stopPath = "$outputPath.stop"
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $outputPath)) | Out-Null
@($outputPath, "$outputPath.meta.json", $scenarioPath, $stopPath) | ForEach-Object {
  Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue
}
$serverScript = Join-Path $PSScriptRoot 'serve-playback-fixture.ps1'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$server = Start-Process -FilePath $pwsh -ArgumentList @(
  '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $serverScript, '-Port', $Port
) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru

try {
  $deadline = [DateTime]::UtcNow.AddSeconds(5)
  do {
    try {
      $response = Invoke-WebRequest -UseBasicParsing -Uri $fixtureUrl -TimeoutSec 1
      if ($response.StatusCode -eq 200) { break }
    } catch {
      Start-Sleep -Milliseconds 100
    }
  } while ([DateTime]::UtcNow -lt $deadline)
  if (-not $response -or $response.StatusCode -ne 200) {
    throw "Playback fixture did not start at $fixtureUrl"
  }

  Write-Host "A dedicated Chrome for Testing window will open at $fixtureUrl"
  Write-Host 'CfT のページ左上にある 4x4 のマゼンタ点を最初にクリックして較正してください。点が消えた後に query を入力し、Submit をクリックしてから、ここで Enter を押します。'
  Push-Location $projectRoot
  $record = $null
  try {
    $record = Start-Process -FilePath (Get-Command deno -ErrorAction Stop).Source -ArgumentList @(
      'task', 'dev', 'record', $outputPath, '--url', $fixtureUrl, '--chrome', $Chrome, '--stop-file', $stopPath
    ) -WorkingDirectory $projectRoot -NoNewWindow -PassThru
    Read-Host '操作後、ここで Enter を押して記録を終了'
    New-Item -ItemType File -Path $stopPath -Force | Out-Null
    $record.WaitForExit()
    if ($record.ExitCode -ne 0) {
      throw "record exited with code $($record.ExitCode)"
    }
    & deno task dev normalize $outputPath --url $fixtureUrl --output $scenarioPath --name fixture-recorded
    if ($LASTEXITCODE -ne 0) {
      throw "normalize exited with code $LASTEXITCODE"
    }
    Write-Host "Wrote normalized scenario: $scenarioPath"
  } finally {
    if ($record -and -not $record.HasExited) {
      New-Item -ItemType File -Path $stopPath -Force | Out-Null
      $record.WaitForExit()
    }
    Remove-Item -LiteralPath $stopPath -Force -ErrorAction SilentlyContinue
    Pop-Location
  }
} finally {
  if (-not $server.HasExited) {
    Stop-Process -Id $server.Id -Force
  }
}
