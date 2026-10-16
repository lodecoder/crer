[CmdletBinding()]
param(
  [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64',
  [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = "dist/$Runtime" }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw "Native input DLL publish failed ($LASTEXITCODE)." }
& (Join-Path $PSScriptRoot 'build-csharp.ps1') -Runtime $Runtime -OutputDirectory $outputPath
$nativeDll = Join-Path $projectRoot "native/bin/Release/net10.0/$Runtime/publish/crer-win-input.dll"
Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $outputPath 'crer-win-input.dll') -Force
Write-Host "Wrote $Runtime Native AOT distribution: $outputPath"
Write-Host 'Set CRER_CHROME (or pass --chrome) to a Chrome for Testing chrome.exe path.'
