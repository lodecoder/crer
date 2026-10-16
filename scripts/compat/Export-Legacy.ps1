[CmdletBinding()]
param([switch] $BuildNative)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$baseline = '50b50fd8bf82a3ce09c25040bca51f60304ba126'
$parent = Join-Path $projectRoot '.crer/compat'
$null = New-Item -ItemType Directory -Path $parent -Force
$id = [Guid]::NewGuid().ToString('N')
$archive = Join-Path $parent "$id.zip"
$destination = Join-Path $parent $id
$originalPath = $env:Path
Push-Location $projectRoot
try {
  & git archive --format=zip "--output=$archive" $baseline
  if ($LASTEXITCODE -ne 0) { throw "Could not export fixed legacy ref $baseline." }
  Expand-Archive -LiteralPath $archive -DestinationPath $destination
  [IO.File]::WriteAllText((Join-Path $destination 'CRER-LEGACY-BASELINE'), $baseline + "`n")
  if ($BuildNative) {
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
    $env:Path = "$vsInstaller;$env:Path"
    & dotnet publish (Join-Path $destination 'native/Crer.WinInput.csproj') -c Release -r win-x64 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not build the comparison-only legacy DLL ($LASTEXITCODE)." }
  }
  Write-Host "Exported comparison-only legacy implementation $baseline to $destination"
  return $destination
} finally { $env:Path = $originalPath; Pop-Location }
