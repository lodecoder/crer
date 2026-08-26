<#
.SYNOPSIS
Installs a Chrome for Testing build for crer and stores its executable path.

.DESCRIPTION
Downloads Chrome for Testing under .crer/browsers with @puppeteer/browsers. The executable path is
persisted in the current user's CRER_CHROME environment variable. Dot-source this script when the
current PowerShell session must receive CRER_CHROME immediately:
  . .\scripts\install-chrome-for-testing.ps1

Use -ChromePath to register an already-downloaded CfT executable without downloading it again.
#>
[CmdletBinding()]
param(
  [string] $InstallRoot,
  [string] $Version = 'stable',
  [string] $ChromePath,
  [switch] $NoPersist
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
  $InstallRoot = Join-Path $repoRoot '.crer\browsers'
}
$root = [System.IO.Path]::GetFullPath($InstallRoot)

New-Item -ItemType Directory -Force -Path $root | Out-Null
if ([string]::IsNullOrWhiteSpace($ChromePath)) {
  if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw 'npx was not found. Install a supported Node.js distribution, then run this script again.'
  }
  & npx @puppeteer/browsers install "chrome@$Version" --path $root
  if ($LASTEXITCODE -ne 0) { throw "Chrome for Testing installation failed (exit code $LASTEXITCODE)." }
  $chrome = Get-ChildItem -LiteralPath $root -Recurse -File -Filter chrome.exe |
    Where-Object { $_.FullName -match 'chrome-win64' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
} else {
  $chrome = Get-Item -LiteralPath $ChromePath -ErrorAction Stop
}

if (-not $chrome -or $chrome.PSIsContainer -or $chrome.Name -ne 'chrome.exe') {
  throw "chrome.exe was not found at '$ChromePath'."
}

$path = $chrome.FullName
$env:CRER_CHROME = $path
$manifestPath = Join-Path $root 'crer-chrome.json'
$env:CRER_CHROME_MANIFEST = $manifestPath
if (-not $NoPersist) {
  [Environment]::SetEnvironmentVariable('CRER_CHROME', $path, 'User')
  [Environment]::SetEnvironmentVariable('CRER_CHROME_MANIFEST', $manifestPath, 'User')
}

$version = $chrome.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = 'unknown' }
$sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
@{ requested = $Version; installed = $version; chrome = $path; sha256 = $sha256 } |
  ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Registered: $version"
Write-Host "SHA-256: $sha256"
Write-Host "CRER_CHROME: $path"
Write-Host "CRER_CHROME_MANIFEST: $manifestPath"
if (-not $NoPersist) {
  Write-Host 'The user environment variable was updated. Open a new PowerShell, or dot-source this script, to use it in another current session.'
}
