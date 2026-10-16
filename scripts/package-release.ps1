<#
.SYNOPSIS
Packages the existing win-x64 Native AOT distribution without rebuilding it.

.DESCRIPTION
Writes crer-win-x64.zip and SHA256SUMS in OutputDirectory. Existing output
files are refused; use a different output directory for another release.
Only the release payload whitelist is copied. Source files and unrelated
files in either directory are left unchanged. Requires PowerShell 5.1 or later.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Version,
  [string] $SourceDirectory = 'dist/win-x64',
  [string] $OutputDirectory = 'dist/releases'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '\A[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}\z') {
  throw 'Version must be a safe release tag: 1-128 ASCII letters, digits, dot, underscore, plus or hyphen, starting with a letter or digit.'
}

function Resolve-PackagePath([string] $Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Package directory paths must not be empty.' }
  if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
  return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}

function Assert-PackageDirectoryChain([string] $Path) {
  # Reject aliases before creating or removing anything. Otherwise an output
  # junction could direct writes or staging cleanup into the published input.
  $current = [IO.Path]::GetFullPath($Path)
  while (-not [string]::IsNullOrEmpty($current)) {
    if (Test-Path -LiteralPath $current) {
      $item = Get-Item -LiteralPath $current -Force
      if (-not $item.PSIsContainer) { throw "Package directory component is not a directory: $current" }
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package directory must not pass through a symbolic link or junction: $current"
      }
    }
    $current = [IO.Path]::GetDirectoryName($current)
  }
}

function Test-PackageWithin([string] $Child, [string] $Parent) {
  $childPath = [IO.Path]::GetFullPath($Child).TrimEnd([char[]]@('\', '/'))
  $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@('\', '/'))
  return $childPath.Equals($parentPath, [StringComparison]::OrdinalIgnoreCase) -or
    $childPath.StartsWith($parentPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$sourcePath = Resolve-PackagePath $SourceDirectory
$outputPath = Resolve-PackagePath $OutputDirectory
$publishedPath = Join-Path $projectRoot 'dist/win-x64'
Assert-PackageDirectoryChain $sourcePath
Assert-PackageDirectoryChain $outputPath
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "Source directory does not exist: $sourcePath" }
if ((Test-PackageWithin $outputPath $sourcePath) -or (Test-PackageWithin $outputPath $publishedPath)) {
  throw 'OutputDirectory must be outside both SourceDirectory and the published dist/win-x64 directory.'
}

$payload = [ordered]@{
  'crer.exe' = Join-Path $sourcePath 'crer.exe'
  'install.ps1' = Join-Path $PSScriptRoot 'install.ps1'
  'uninstall.ps1' = Join-Path $PSScriptRoot 'uninstall.ps1'
  'install-common.ps1' = Join-Path $PSScriptRoot 'install-common.ps1'
  'install-chrome-for-testing.ps1' = Join-Path $PSScriptRoot 'install-chrome-for-testing.ps1'
  'chrome-for-testing-metadata.ps1' = Join-Path $PSScriptRoot 'chrome-for-testing-metadata.ps1'
  'README.en.md' = Join-Path $projectRoot 'docs/user-guide.en.md'
  'README.ja.md' = Join-Path $projectRoot 'docs/user-guide.ja.md'
}
foreach ($entry in $payload.GetEnumerator()) {
  if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Required release input is missing: $($entry.Value)" }
}
$license = Join-Path $projectRoot 'LICENSE'
if (Test-Path -LiteralPath $license -PathType Leaf) { $payload['LICENSE'] = $license }

$zipName = 'crer-win-x64.zip'
$zipPath = Join-Path $outputPath $zipName
$checksumPath = Join-Path $outputPath 'SHA256SUMS'
foreach ($target in @($zipPath, $checksumPath)) {
  if (Test-Path -LiteralPath $target) { throw "Refusing to replace existing release output: $target" }
}

$null = [IO.Directory]::CreateDirectory($outputPath)
$stagingName = '.crer-package-' + [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $outputPath $stagingName
$null = New-Item -ItemType Directory -Path $stagingRoot
try {
  $filesPath = Join-Path $stagingRoot 'files'
  $null = [IO.Directory]::CreateDirectory($filesPath)
  foreach ($entry in $payload.GetEnumerator()) {
    $target = Join-Path $filesPath $entry.Key
    if ($entry.Key -in @('README.en.md', 'README.ja.md')) {
      $guide = [IO.File]::ReadAllText($entry.Value).Replace('(user-guide.en.md)', '(README.en.md)').Replace('(user-guide.ja.md)', '(README.ja.md)')
      [IO.File]::WriteAllText($target, $guide, [Text.UTF8Encoding]::new($false))
    } else { Copy-Item -LiteralPath $entry.Value -Destination $target }
  }
  $utf8 = [Text.UTF8Encoding]::new($false)
  $release = [ordered]@{ repository = 'lodecoder/crer'; version = $Version; runtime = 'win-x64' }
  [IO.File]::WriteAllText((Join-Path $filesPath 'release.json'), ($release | ConvertTo-Json -Compress) + "`n", $utf8)
  $stagedZip = Join-Path $stagingRoot $zipName
  [IO.Compression.ZipFile]::CreateFromDirectory($filesPath, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
  $sha256 = (Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256).Hash.ToLowerInvariant()
  $stagedChecksum = Join-Path $stagingRoot 'SHA256SUMS'
  [IO.File]::WriteAllText($stagedChecksum, "$sha256  $zipName`n", $utf8)

  # Move on the same volume and do not overwrite even if another packager
  # created an output after the preflight check. A failed second move leaves
  # the completed ZIP intact rather than deleting a possibly modified output.
  [IO.File]::Move($stagedZip, $zipPath)
  [IO.File]::Move($stagedChecksum, $checksumPath)
  Write-Host "Packaged win-x64 release '$Version': $zipPath"
  [pscustomobject]@{ Version = $Version; Runtime = 'win-x64'; Zip = $zipPath; Checksums = $checksumPath; SHA256 = $sha256 }
}
finally {
  # Remove only this invocation's unique staging directory. Never recursively
  # remove the output root, source distribution, or any existing release.
  $resolvedStage = [IO.Path]::GetFullPath($stagingRoot)
  $resolvedOutput = [IO.Path]::GetFullPath($outputPath).TrimEnd([char[]]@('\', '/'))
  $stageParent = [IO.Path]::GetDirectoryName($resolvedStage).TrimEnd([char[]]@('\', '/'))
  if (-not $resolvedStage.StartsWith($resolvedOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
      $stageParent -ne $resolvedOutput -or
      [IO.Path]::GetFileName($resolvedStage) -ne $stagingName -or
      $stagingName -notmatch '\A\.crer-package-[0-9a-f]{32}\z') {
    throw "Refusing to clean an unverified staging path: $resolvedStage"
  }
  Assert-PackageDirectoryChain $resolvedStage
  if (Test-Path -LiteralPath $resolvedStage -PathType Container) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
