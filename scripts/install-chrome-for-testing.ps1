<#
.SYNOPSIS
Installs the current Chrome for Testing Stable build for crer and stores its executable path.

.DESCRIPTION
Downloads Chrome for Testing under .crer/browsers with @puppeteer/browsers. The executable path is
persisted in the current user's CRER_CHROME environment variable. Dot-source this script when the
current PowerShell session must receive CRER_CHROME immediately:
  . .\scripts\install-chrome-for-testing.ps1
#>
[CmdletBinding()]
param(
  [string] $InstallRoot = (Join-Path (Get-Location) '.crer\browsers'),
  [switch] $NoPersist
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($InstallRoot)

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
  throw 'npx was not found. Install a supported Node.js distribution, then run this script again.'
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
& npx @puppeteer/browsers install chrome@stable --path $root
if ($LASTEXITCODE -ne 0) { throw "Chrome for Testing installation failed (exit code $LASTEXITCODE)." }

$chrome = Get-ChildItem -LiteralPath $root -Recurse -File -Filter chrome.exe |
  Where-Object { $_.FullName -match 'chrome-win64' } |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1

if (-not $chrome) { throw "chrome.exe was not found below $root after installation." }

$path = $chrome.FullName
$env:CRER_CHROME = $path
if (-not $NoPersist) {
  [Environment]::SetEnvironmentVariable('CRER_CHROME', $path, 'User')
}

$version = (& $path --version).Trim()
Write-Host "Installed: $version"
Write-Host "CRER_CHROME: $path"
if (-not $NoPersist) {
  Write-Host 'The user environment variable was updated. Open a new PowerShell, or dot-source this script, to use it in another current session.'
}