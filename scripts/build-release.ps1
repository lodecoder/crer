[CmdletBinding()]
param(
  [ValidateSet('win-x64', 'win-arm64')]
  [string] $Runtime = 'win-x64',
  [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = "dist\\$Runtime"
}
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$nativeDll = Join-Path $repoRoot "native\\bin\\Release\\net10.0\\$Runtime\\publish\\crer-win-input.dll"
$denoTarget = switch ($Runtime) {
  'win-x64' { 'x86_64-pc-windows-msvc' }
  'win-arm64' { 'aarch64-pc-windows-msvc' }
}

Push-Location $repoRoot
try {
  & (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
  $exePath = Join-Path $outputPath 'crer.exe'
  deno compile --target $denoTarget --allow-env --allow-ffi --allow-net --allow-read --allow-run --allow-write `
    --output $exePath src\main.ts
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $outputPath 'crer-win-input.dll') -Force
  Write-Host "Wrote $Runtime standalone distribution: $outputPath"
  Write-Host 'Set CRER_CHROME (or pass --chrome) to a Chrome for Testing chrome.exe path before running crer.exe.'
} finally {
  Pop-Location
}
