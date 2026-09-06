[CmdletBinding()]
param(
  [int] $Port = 8080,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $Plan = 'fixtures\playback\session-reuse.crer.plan.yaml',
  [string] $Profile = '.crer\profiles\session-reuse-fixture'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}
$before = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot '.crer\runs') -Directory -ErrorAction SilentlyContinue |
  ForEach-Object FullName)
$server = Start-Process -FilePath (Get-Command pwsh -ErrorAction Stop).Source -ArgumentList @(
  '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'serve-playback-fixture.ps1'), '-Port', $Port
) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
try {
  $url = "http://127.0.0.1:$Port/index.html"
  $deadline = [DateTime]::UtcNow.AddSeconds(5)
  do {
    try { $ready = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 1 $url).StatusCode -eq 200 } catch { $ready = $false; Start-Sleep -Milliseconds 100 }
  } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
  if (-not $ready) { throw "Playback fixture did not start at $url" }
  Push-Location $projectRoot
  try { & deno task dev run $Plan --chrome $Chrome --profile-dir $Profile } finally { Pop-Location }
  if ($LASTEXITCODE -ne 0) { throw "run exited with code $LASTEXITCODE (expected 0)" }

  $runs = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot '.crer\runs') -Directory |
    Where-Object { $_.FullName -notin $before })
  if ($runs.Count -ne 2) { throw "Plan created $($runs.Count) run directories (expected 2)." }
  $metadata = @($runs | ForEach-Object {
    Get-Content -LiteralPath (Join-Path $_.FullName 'run.json') -Raw | ConvertFrom-Json
  })
  if (@($metadata | Where-Object { $_.browserSession.reused -eq $false }).Count -ne 1 -or
      @($metadata | Where-Object { $_.browserSession.reused -eq $true }).Count -ne 1) {
    throw 'run.json did not report one new and one reused browser session.'
  }
  $first = $runs | Where-Object {
    (Get-Content -LiteralPath (Join-Path $_.FullName 'run.json') -Raw | ConvertFrom-Json).browserSession.reused -eq $false
  }
  $last = $runs | Where-Object {
    (Get-Content -LiteralPath (Join-Path $_.FullName 'run.json') -Raw | ConvertFrom-Json).browserSession.reused -eq $true
  }
  if (Test-Path -LiteralPath (Join-Path $first.FullName 'shutdown.json')) {
    throw 'CfT was closed after the first scenario instead of being reused.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $last.FullName 'shutdown.json') -PathType Leaf)) {
    throw 'The reused CfT session did not leave its final shutdown artifact.'
  }
  Write-Host "PASS: two scenarios reused one CfT window; artifacts: $($runs.FullName -join ', ')"
} finally {
  if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
