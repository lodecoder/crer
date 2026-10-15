[CmdletBinding()]
param([switch] $NoAot)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$originalPath = $env:Path
Push-Location $projectRoot
try {
  $env:Path = "$vsInstaller;$env:Path"
  $aot = if ($NoAot) { 'false' } else { 'true' }
  dotnet publish csharp/Crer.Cli/Crer.Cli.csproj -c Release -r win-x64 --self-contained true "-p:PublishAot=$aot" -o dist/csharp/win-x64
  if ($LASTEXITCODE -ne 0) { throw "C# publish failed ($LASTEXITCODE)" }
} finally {
  $env:Path = $originalPath
  Pop-Location
}
