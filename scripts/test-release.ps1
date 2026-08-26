[CmdletBinding()]
param(
  [string] $Chrome = $env:CRER_CHROME,
  [string] $OutputDirectory = 'dist\\win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}

& (Join-Path $PSScriptRoot 'build-release.ps1') -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exePath = Join-Path ([System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))) 'crer.exe'
& (Join-Path $PSScriptRoot 'test-playback-fixture.ps1') -Chrome $Chrome -CrerExe $exePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "PASS: standalone distribution playback completed: $exePath"
