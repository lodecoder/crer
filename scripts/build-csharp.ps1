[CmdletBinding()]
param(
  [ValidateSet('win-x64', 'win-arm64')][string] $Runtime = 'win-x64',
  [string] $OutputDirectory,
  [switch] $NoAot
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = if ($NoAot) { "dist/development/$Runtime" } else { "dist/$Runtime" }
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
$originalPath = $env:Path
Push-Location $projectRoot
try {
  $env:Path = "$vsInstaller;$env:Path"
  $aot = if ($NoAot) { 'false' } else { 'true' }
  & dotnet publish src/Crer.Cli/Crer.Cli.csproj -c Release -r $Runtime --self-contained true "-p:PublishAot=$aot" -o $outputPath
  if ($LASTEXITCODE -ne 0) { throw "C# publish failed ($LASTEXITCODE)" }
} finally { $env:Path = $originalPath; Pop-Location }
