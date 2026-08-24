[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhereDirectory = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $vswhereDirectory 'vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
  throw "vswhere.exe was not found at '$vswhere'. Install Visual Studio Build Tools with the C++ desktop development workload."
}

# VS 2026's vcvarsall.bat invokes vswhere.exe by its filename. Native AOT invokes
# vcvarsall.bat while locating link.exe, so make its directory available to that child process.
if (($env:Path -split ';') -notcontains $vswhereDirectory) {
  $env:Path = "$vswhereDirectory;$env:Path"
}

Push-Location $repoRoot
try {
  dotnet publish native\Crer.WinInput.csproj -c Release -r win-x64
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
} finally {
  Pop-Location
}
