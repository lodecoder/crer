<#
.SYNOPSIS
Installs a Chrome for Testing build for crer and stores its executable path.

.DESCRIPTION
Downloads win64 Chrome for Testing from its official JSON metadata with PowerShell/BCL. The executable path is
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
. (Join-Path $PSScriptRoot 'chrome-for-testing-metadata.ps1')
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
  $InstallRoot = Join-Path $repoRoot '.crer\browsers'
}
$root = [System.IO.Path]::GetFullPath($InstallRoot)

New-Item -ItemType Directory -Force -Path $root | Out-Null
$requestedVersion = $Version
if ([string]::IsNullOrWhiteSpace($ChromePath)) {
  $download = Resolve-CrerChromeForTesting -Requested $requestedVersion
  $installation = Join-Path (Join-Path $root 'chrome') ('win64-' + $download.version)
  $installedExe = Join-Path $installation 'chrome-win64/chrome.exe'
  if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
    # An existing version is never overwritten or selected by unrelated file mtimes.
    $chrome = Get-Item -LiteralPath $installedExe
  } else {
    if (Test-Path -LiteralPath $installation) {
      throw "Installation directory already exists without chrome.exe: $installation. Choose a different -InstallRoot to preserve its existing contents."
    }
    $downloadId = [Guid]::NewGuid().ToString('N')
    $archive = Join-Path $root ('.chrome-download-' + $downloadId + '.zip')
    $staging = Join-Path $root ('.chrome-extract-' + $downloadId)
    Assert-CrerInstallChildPath -Path $archive -Root $root
    Assert-CrerInstallChildPath -Path $staging -Root $root
    try {
      Write-Host "Downloading Chrome for Testing $($download.version) (win64)"
      Invoke-WebRequest -Uri $download.url -OutFile $archive -TimeoutSec 600
      New-Item -ItemType Directory -Path $staging | Out-Null
      $zip = [IO.Compression.ZipFile]::OpenRead($archive)
      try {
        foreach ($entry in $zip.Entries) {
          $entryPath = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
          Assert-CrerInstallChildPath -Path $entryPath -Root $staging
          # Expand-Archive is used only for regular files/directories in this official ZIP.
          if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Chrome archive contained an unexpected symbolic link.' }
        }
      } finally { $zip.Dispose() }
      Expand-Archive -LiteralPath $archive -DestinationPath $staging
      $stagedExe = Join-Path $staging 'chrome-win64/chrome.exe'
      if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) { throw 'Chrome for Testing ZIP did not contain chrome-win64/chrome.exe.' }
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installation) | Out-Null
      # Check both resolved absolute paths before moving our unique extraction directory.
      Assert-CrerInstallChildPath -Path $staging -Root $root
      Assert-CrerInstallChildPath -Path $installation -Root $root
      [IO.Directory]::Move($staging, $installation)
      $chrome = Get-Item -LiteralPath $installedExe
    } finally {
      if (Test-Path -LiteralPath $archive) {
        Assert-CrerInstallChildPath -Path $archive -Root $root
        Remove-Item -LiteralPath $archive -Force
      }
      if (Test-Path -LiteralPath $staging) {
        Assert-CrerInstallChildPath -Path $staging -Root $root
        Remove-Item -LiteralPath $staging -Recurse -Force
      }
    }
  }
} else {
  $chrome = Get-Item -LiteralPath $ChromePath -ErrorAction Stop
}

if (-not $chrome -or $chrome.PSIsContainer -or $chrome.Name -ne 'chrome.exe') {
  throw "chrome.exe was not found at '$ChromePath'."
}

$path = $chrome.FullName
$installedVersion = $chrome.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($installedVersion)) { $installedVersion = 'unknown' }
if ([string]::IsNullOrWhiteSpace($ChromePath) -and $installedVersion -cne $download.version) {
  throw "Chrome for Testing executable version '$installedVersion' does not match the resolved version '$($download.version)'. Existing files were preserved."
}
$sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestPath = Join-Path $root 'crer-chrome.json'
@{ requested = $requestedVersion; installed = $installedVersion; chrome = $path; sha256 = $sha256 } |
  ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
$env:CRER_CHROME = $path
$env:CRER_CHROME_MANIFEST = $manifestPath
if (-not $NoPersist) {
  [Environment]::SetEnvironmentVariable('CRER_CHROME', $path, 'User')
  [Environment]::SetEnvironmentVariable('CRER_CHROME_MANIFEST', $manifestPath, 'User')
}

Write-Host "Registered: $installedVersion"
Write-Host "SHA-256: $sha256"
Write-Host "CRER_CHROME: $path"
Write-Host "CRER_CHROME_MANIFEST: $manifestPath"
if (-not $NoPersist) {
  Write-Host 'The user environment variable was updated. Open a new PowerShell, or dot-source this script, to use it in another current session.'
}
