<#
.SYNOPSIS
Removes tracked crer release files and the PATH entry added by its installer.
.DESCRIPTION
Preserves modified/untracked files, Chrome for Testing, CRER_CHROME settings,
and all scenarios, profiles and artifacts in working directories.
#>
[CmdletBinding()]
param([string] $InstallDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-common.ps1')
$directory = Get-CrerInstallDirectory $InstallDirectory
$state = Read-CrerInstallState $directory
if ($null -eq $state) { throw "No managed crer installation was found: $directory" }
$running = @(Get-Process -Name crer -ErrorAction SilentlyContinue | Where-Object {
  try { $_.Path -ieq (Join-Path $directory 'crer.exe') } catch { $false }
})
if ($running.Count -gt 0) { throw 'Close crer before uninstalling.' }
$remove = [Collections.Generic.List[string]]::new()
$preserved = [Collections.Generic.List[string]]::new()
foreach ($entry in $state.files) {
  $file = Join-Path $directory $entry.name
  if (Test-Path -LiteralPath $file) {
    if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-CrerFileDigest $file) -ceq $entry.sha256) { $remove.Add($file) }
    else { $preserved.Add($file) }
  }
}
if ($state.pathAdded) {
  $pathState = Get-CrerUserPathState
  $userPath = $pathState.value
  $newUserPath = Remove-CrerPathEntry $userPath $directory
  if ($newUserPath -cne $userPath) {
    $exists = -not ($state.pathCreated -and [string]::IsNullOrEmpty($newUserPath))
    Set-CrerUserPathState ([pscustomobject]@{ exists = $exists; value = $newUserPath; kind = $pathState.kind })
  }
  $env:Path = Remove-CrerPathEntry $env:Path $directory
}
$helperNames = @('uninstall.ps1', 'install-common.ps1')
$errors = [Collections.Generic.List[string]]::new()
# Retain the scripts and state until all other removals finish, allowing a retry after a locked file.
foreach ($file in $remove) {
  if ([IO.Path]::GetFileName($file) -in $helperNames) { continue }
  try {
    Assert-CrerPlainPath $file
    Remove-Item -LiteralPath $file -Force
  } catch { $errors.Add($_.Exception.Message) }
}
if ($errors.Count -gt 0) { throw "Some files could not be removed. Close crer and any open installed files, then retry uninstall.ps1. $($errors -join ' ')" }
if ($preserved.Count -eq 0) {
  $helperBytes = @{}
  foreach ($name in $helperNames) {
    $file = Join-Path $directory $name
    if (Test-Path -LiteralPath $file -PathType Leaf) { $helperBytes[$name] = [IO.File]::ReadAllBytes($file) }
  }
  try {
    foreach ($name in $helperBytes.Keys) { Remove-Item -LiteralPath (Join-Path $directory $name) -Force }
    Remove-Item -LiteralPath (Join-Path $directory '.crer-install.json') -Force
  } catch {
    foreach ($name in $helperBytes.Keys) {
      $file = Join-Path $directory $name
      if (-not (Test-Path -LiteralPath $file)) { [IO.File]::WriteAllBytes($file, $helperBytes[$name]) }
    }
    throw
  }
  if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
    # The resolved directory is the state-bound install directory, and is empty. Never recurse here.
    Assert-CrerPlainPath $directory
    Remove-Item -LiteralPath $directory
  }
} else {
  Write-Warning ('Preserved modified files; installation state and removal scripts retained: ' + ($preserved -join ', '))
}
Write-Host "Removed tracked crer files: $directory"
Write-Host 'Chrome for Testing, Chrome environment settings, scenarios, profiles and artifacts were preserved.'
