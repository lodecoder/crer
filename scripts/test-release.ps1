[CmdletBinding()]
param(
  [string] $Chrome = $env:CRER_CHROME,
  [ValidateSet('win-x64', 'win-arm64')]
  [string] $Runtime = 'win-x64',
  [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = "dist\\$Runtime"
}
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) {
  throw 'Set CRER_CHROME or pass -Chrome with the Chrome for Testing chrome.exe path.'
}

& (Join-Path $PSScriptRoot 'build-release.ps1') -Runtime $Runtime -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exePath = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory, $projectRoot)) 'crer.exe'
& (Join-Path $PSScriptRoot 'test-playback-fixture.ps1') -Chrome $Chrome -CrerExe $exePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "PASS: standalone distribution playback completed: $exePath"
