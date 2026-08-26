[CmdletBinding()]
param(
  [string] $OutputDirectory = 'dist\win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$nativeDll = Join-Path $repoRoot 'native\bin\Release\net10.0\win-x64\publish\crer-win-input.dll'

Push-Location $repoRoot
try {
  & (Join-Path $PSScriptRoot 'build-native.ps1')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
  $exePath = Join-Path $outputPath 'crer.exe'
  deno compile --allow-env --allow-ffi --allow-net --allow-read --allow-run --allow-write `
    --output $exePath src\main.ts
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $outputPath 'crer-win-input.dll') -Force
  Write-Host "Wrote standalone distribution: $outputPath"
  Write-Host 'Set CRER_CHROME (or pass --chrome) to a Chrome for Testing chrome.exe path before running crer.exe.'
} finally {
  Pop-Location
}
