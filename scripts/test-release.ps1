[CmdletBinding()]
param(
  [string] $Chrome = $env:CRER_CHROME,
  [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64',
  [string] $OutputDirectory,
  [int] $Port = 8080
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = "dist/$Runtime" }
if ([string]::IsNullOrWhiteSpace($Chrome) -or -not (Test-Path -LiteralPath $Chrome -PathType Leaf)) { throw 'Set CRER_CHROME or pass -Chrome with a Chrome for Testing executable.' }
& (Join-Path $PSScriptRoot 'build-release.ps1') -Runtime $Runtime -OutputDirectory $OutputDirectory
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
$exePath = Join-Path $outputPath 'crer.exe'
foreach ($scenario in @('search', 'keyboard-submit', 'drag')) {
  & (Join-Path $PSScriptRoot 'test-playback-fixture.ps1') -Port $Port -Chrome $Chrome -CrerExe $exePath -Scenario "fixtures/playback/$scenario.crer.yaml"
}
& (Join-Path $PSScriptRoot 'test-plan-fixture.ps1') -Port $Port -Chrome $Chrome -CrerExe $exePath
& (Join-Path $PSScriptRoot 'test-session-reuse-fixture.ps1') -Port $Port -Chrome $Chrome -CrerExe $exePath
Write-Host "PASS Native AOT distribution playback: $exePath"
