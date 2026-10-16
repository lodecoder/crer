[CmdletBinding()]
param([ValidateRange(1, 65535)][int] $Port = 8080)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  throw 'Node.js 24 was not found.'
}

Push-Location $projectRoot
try { & node (Join-Path $PSScriptRoot 'serve-playback-fixture.mjs') $Port } finally { Pop-Location }
