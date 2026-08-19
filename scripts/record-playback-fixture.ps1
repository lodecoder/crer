[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Output = '.crer\\fixture.raw-input.ndjson',
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
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $outputPath)) | Out-Null
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
  Write-Host 'Type a query, click Submit, then press Ctrl+C in this terminal to finish recording.'
  Push-Location $projectRoot
  try {
    & deno task dev record $outputPath --url $fixtureUrl --chrome $Chrome
  } finally {
    Pop-Location
  }
} finally {
  if (-not $server.HasExited) {
    Stop-Process -Id $server.Id -Force
  }
}
