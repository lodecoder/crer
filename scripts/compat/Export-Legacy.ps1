[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$baseline = '50b50fd8bf82a3ce09c25040bca51f60304ba126'
$parent = Join-Path $projectRoot '.crer/compat'
$null = New-Item -ItemType Directory -Path $parent -Force
$id = [Guid]::NewGuid().ToString('N')
$archive = Join-Path $parent "$id.zip"
$destination = Join-Path $parent $id
Push-Location $projectRoot
try {
  & git archive --format=zip "--output=$archive" $baseline
  if ($LASTEXITCODE -ne 0) { throw "Could not export fixed legacy ref $baseline." }
  Expand-Archive -LiteralPath $archive -DestinationPath $destination
  [IO.File]::WriteAllText((Join-Path $destination 'CRER-LEGACY-BASELINE'), $baseline + "`n")
  Write-Host "Exported comparison-only legacy implementation $baseline to $destination"
  return $destination
} finally { Pop-Location }
