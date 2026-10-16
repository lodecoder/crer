[CmdletBinding()]
param(
  [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64',
  [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = "dist/$Runtime" }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
& (Join-Path $PSScriptRoot 'build-csharp.ps1') -Runtime $Runtime -OutputDirectory $outputPath
$retiredDll = Join-Path $outputPath 'crer-win-input.dll'
if (Test-Path -LiteralPath $retiredDll) {
  $retiredFile = Get-Item -LiteralPath $retiredDll -Force
  if ($retiredFile.PSIsContainer -or ($retiredFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
      $retiredFile.FullName -ine [IO.Path]::GetFullPath($retiredDll) -or
      [IO.Path]::GetDirectoryName($retiredFile.FullName) -ine $outputPath.TrimEnd('\', '/')) {
    throw "Refusing to remove an unexpected retired DLL path: $retiredDll"
  }
  Remove-Item -LiteralPath $retiredFile.FullName -Force
}
Write-Host "Wrote $Runtime Native AOT distribution: $outputPath"
Write-Host 'Set CRER_CHROME (or pass --chrome) to a Chrome for Testing chrome.exe path.'
