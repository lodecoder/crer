<#
.SYNOPSIS
Installs an extracted crer Windows x64 release for the current user.
.EXAMPLE
.\install.ps1
.EXAMPLE
.\install.ps1 -InstallDirectory C:\Tools\crer -NoPath
#>
[CmdletBinding()]
param(
  [string] $InstallDirectory,
  [string] $SourceDirectory = $PSScriptRoot,
  [switch] $NoPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-common.ps1')
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
  throw 'This release supports Windows x64 only.'
}
$destination = Get-CrerInstallDirectory $InstallDirectory
$source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
Assert-CrerPlainPath $source
if ($source -ieq $destination) { throw 'Extract the release to a separate folder before installing.' }
$files = @(Get-CrerReleaseFiles)
if (Test-Path -LiteralPath (Join-Path $source 'LICENSE') -PathType Leaf) { $files += 'LICENSE' }
foreach ($name in $files) {
  $file = Join-Path $source $name
  Assert-CrerPlainPath $file
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file missing: $file. Extract crer-win-x64.zip first." }
}
$release = Get-Content -LiteralPath (Join-Path $source 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($release.repository -cne 'lodecoder/crer' -or $release.runtime -cne 'win-x64' -or
    [string]::IsNullOrWhiteSpace($release.version)) { throw 'Not a crer Windows x64 release.' }
$previous = Read-CrerInstallState $destination
if (Test-Path -LiteralPath $destination) {
  if (-not (Test-Path -LiteralPath $destination -PathType Container)) { throw "Not a directory: $destination" }
  if ($null -eq $previous -and @(Get-ChildItem -LiteralPath $destination -Force).Count -gt 0) {
    throw "InstallDirectory is not empty and is not managed by this installer: $destination"
  }
}
$previousFiles = @{}
if ($null -ne $previous) {
  foreach ($entry in $previous.files) { $previousFiles[$entry.name] = $entry.sha256 }
}
foreach ($name in $files) {
  $target = Join-Path $destination $name
  Assert-CrerPlainPath $target
  if (Test-Path -LiteralPath $target) {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        -not $previousFiles.ContainsKey($name) -or (Get-CrerFileDigest $target) -cne $previousFiles[$name]) {
      throw "Existing file was not installed by crer or was modified; preserve it before updating: $target"
    }
  }
}
# 実行ファイルを置き換える前に、実行中のコピーがないことを確認する。
$running = @(Get-Process -Name crer -ErrorAction SilentlyContinue | Where-Object {
  try { $_.Path -ieq (Join-Path $destination 'crer.exe') } catch { $false }
})
if ($running.Count -gt 0) { throw 'Close crer before installing or updating.' }
$createdDirectory = -not (Test-Path -LiteralPath $destination)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$stage = Join-Path $destination ('.install-' + [Guid]::NewGuid().ToString('N'))
Assert-CrerPlainPath $stage
New-Item -ItemType Directory -Path $stage | Out-Null
$backups = Join-Path $stage 'backup'
$incoming = Join-Path $stage 'incoming'
New-Item -ItemType Directory -Path $backups, $incoming | Out-Null
$changed = [Collections.Generic.List[string]]::new()
$oldPathState = Get-CrerUserPathState
$oldUserPath = $oldPathState.value
$oldProcessPath = $env:Path
$pathWritten = $false
$committed = $false
$rollbackFailed = $false
try {
  foreach ($name in $files) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $incoming $name)
  }
  $tracked = @($files | ForEach-Object { [ordered]@{ name = $_; sha256 = Get-CrerFileDigest (Join-Path $incoming $_) } })
  # Keep ownership of an optional file absent from the next release; never delete it during upgrade.
  foreach ($name in $previousFiles.Keys) {
    if ($files -cnotcontains $name) { $tracked += [ordered]@{ name = $name; sha256 = $previousFiles[$name] } }
  }
  $addedPath = ($null -ne $previous -and $previous.pathAdded)
  $newUserPath = $oldUserPath
  if (-not $NoPath -and -not (Test-CrerPathContains $oldUserPath $destination)) {
    if ([string]::IsNullOrEmpty($oldUserPath)) { $newUserPath = $destination }
    else { $newUserPath = $oldUserPath + ';' + $destination }
    $addedPath = $true
  }
  $pathCreated = ($null -ne $previous -and $previous.pathCreated) -or ($newUserPath -cne $oldUserPath -and -not $oldPathState.exists)
  $state = [ordered]@{ product = 'crer'; schema = 1; directory = $destination; version = $release.version; pathAdded = [bool] $addedPath; pathCreated = [bool] $pathCreated; files = $tracked }
  [IO.File]::WriteAllText((Join-Path $incoming '.crer-install.json'), ($state | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
  foreach ($name in ($files + @('.crer-install.json'))) {
    $target = Join-Path $destination $name
    if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination (Join-Path $backups $name) }
    $changed.Add($name)
    Copy-Item -LiteralPath (Join-Path $incoming $name) -Destination $target -Force
  }
  if ($newUserPath -cne $oldUserPath) {
    $newPathState = [pscustomobject]@{ exists = $true; value = $newUserPath; kind = $oldPathState.kind }
    Set-CrerUserPathState $newPathState
    $pathWritten = $true
  }
  if (-not $NoPath -and -not (Test-CrerPathContains $env:Path $destination)) { $env:Path = $env:Path + ';' + $destination }
  $committed = $true
} catch {
  $failure = $_
  $rollbackErrors = [Collections.Generic.List[string]]::new()
  foreach ($name in $changed) {
    $target = Join-Path $destination $name
    $backup = Join-Path $backups $name
    try {
      if (Test-Path -LiteralPath $backup) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-CrerFileDigest $target) -cne (Get-CrerFileDigest $backup)) {
          Copy-Item -LiteralPath $backup -Destination $target -Force
        }
      } elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
    } catch { $rollbackErrors.Add($_.Exception.Message) }
  }
  if ($pathWritten) {
    try { Set-CrerUserPathState $oldPathState }
    catch { $rollbackErrors.Add($_.Exception.Message) }
  }
  $env:Path = $oldProcessPath
  if ($rollbackErrors.Count -gt 0) {
    $rollbackFailed = $true
    throw "Installation failed: $($failure.Exception.Message) Rollback requires attention; backups preserved at $stage. $($rollbackErrors -join ' ')"
  }
  throw $failure
} finally {
  # Only our unique staging directory is removed recursively, after a fresh absolute path check.
  $stageFull = [IO.Path]::GetFullPath($stage)
  if (-not $stageFull.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary directory escapes InstallDirectory.' }
  Assert-CrerPlainPath $stageFull
  if (-not $rollbackFailed) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
  if (-not $committed -and $createdDirectory -and @(Get-ChildItem -LiteralPath $destination -Force).Count -eq 0) {
    Remove-Item -LiteralPath $destination
  }
}
Write-Host "Installed crer $($release.version): $destination"
if (-not $NoPath) { Write-Host 'User PATH is ready. Open a new PowerShell for other sessions, then run: crer help' }
Write-Host 'Next: configure Chrome for Testing with the bundled install-chrome-for-testing.ps1 (see README.en.md / README.ja.md).'
