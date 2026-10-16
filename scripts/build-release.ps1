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
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
$nativeDll = Join-Path $repoRoot "native\\bin\\Release\\net10.0\\$Runtime\\publish\\crer-win-input.dll"

Push-Location $repoRoot
try {
  & node -e "if (process.platform !== 'win32' || process.versions.node.split('.')[0] !== '24' || process.arch !== process.argv[1]) { console.error('対象アーキテクチャと一致する Windows 版 Node.js 24 が必要です。'); process.exit(1); }" ($Runtime -replace '^win-', '')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  & (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  & npm ci
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  & node scripts/build-node.mjs --runtime $Runtime --output $outputPath
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $outputPath 'crer-win-input.dll') -Force
  Write-Host "Wrote $Runtime standalone distribution: $outputPath"
  Write-Host 'Set CRER_CHROME (or pass --chrome) to a Chrome for Testing chrome.exe path before running crer.exe.'
} finally {
  Pop-Location
}
