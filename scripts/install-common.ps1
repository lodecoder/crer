# Shared by the per-user release installer and uninstaller. PowerShell 5.1+.
function Get-CrerInstallDirectory {
  param([string] $Directory)
  if ([string]::IsNullOrWhiteSpace($Directory)) {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable. Specify -InstallDirectory.' }
    $Directory = Join-Path $env:LOCALAPPDATA 'Programs\crer'
  }
  $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
  if ($full -notmatch '^[A-Za-z]:\\' -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) {
    throw 'InstallDirectory must be a local directory below a drive root.'
  }
  Assert-CrerPlainPath $full
  return $full
}

function Assert-CrerPlainPath {
  param([Parameter(Mandatory)][string] $Path)
  $current = [IO.Path]::GetFullPath($Path)
  while (-not [string]::IsNullOrEmpty($current)) {
    $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
    if ($null -ne $item) {
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installation paths cannot contain symbolic links or junctions: $current"
      }
    }
    $parent = [IO.Path]::GetDirectoryName($current)
    if ($parent -eq $current) { break }
    $current = $parent
  }
}

function Get-CrerReleaseFiles {
  return @('crer.exe', 'crer-win-input.dll', 'install.ps1', 'uninstall.ps1', 'install-common.ps1',
    'install-chrome-for-testing.ps1', 'chrome-for-testing-metadata.ps1', 'README.en.md', 'README.ja.md', 'release.json')
}

function Get-CrerFileDigest {
  param([Parameter(Mandatory)][string] $Path)
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-CrerInstallState {
  param([Parameter(Mandatory)][string] $Directory)
  $statePath = Join-Path $Directory '.crer-install.json'
  if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
  Assert-CrerPlainPath $statePath
  $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($state.product -cne 'crer' -or $state.schema -ne 1 -or
      $state.directory -ine $Directory -or $state.pathAdded -isnot [bool]) {
    throw "Unrecognized installation state: $statePath"
  }
  $allowed = @(Get-CrerReleaseFiles) + @('LICENSE')
  $seen = @{}
  if (@($state.files).Count -eq 0) { throw 'Installation state has no tracked files.' }
  foreach ($file in $state.files) {
    if ($allowed -cnotcontains $file.name -or $seen.ContainsKey([string] $file.name) -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
      throw 'Installation state contains an invalid or duplicate file entry.'
    }
    $seen[[string] $file.name] = $true
    Assert-CrerPlainPath (Join-Path $Directory $file.name)
  }
  return $state
}

function Test-CrerPathEntry {
  param([string] $Entry, [string] $Directory)
  if ([string]::IsNullOrWhiteSpace($Entry)) { return $false }
  try {
    $expanded = [Environment]::ExpandEnvironmentVariables($Entry.Trim().Trim('"'))
    return [IO.Path]::GetFullPath($expanded).TrimEnd('\', '/') -ieq $Directory
  } catch { return $false }
}

function Test-CrerPathContains {
  param([string] $PathValue, [string] $Directory)
  foreach ($entry in ($PathValue -split ';')) {
    if (Test-CrerPathEntry $entry $Directory) { return $true }
  }
  return $false
}

function Remove-CrerPathEntry {
  param([string] $PathValue, [string] $Directory)
  $kept = @($PathValue -split ';' | Where-Object { -not (Test-CrerPathEntry $_ $Directory) })
  return $kept -join ';'
}

function Get-CrerRegistryPathState {
  param([Microsoft.Win32.RegistryKey] $Key)
  $exists = $null -ne $Key -and @($Key.GetValueNames()) -icontains 'Path'
  if ($exists) {
    $kind = $Key.GetValueKind('Path')
    if ($kind -notin @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
      throw 'User PATH is not a string registry value. No PATH changes were made.'
    }
    $value = $Key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    return [pscustomobject]@{ exists = $true; value = [string] $value; kind = $kind }
  }
  return [pscustomobject]@{ exists = $false; value = ''; kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
}

function Set-CrerRegistryPathState {
  param([Microsoft.Win32.RegistryKey] $Key, [object] $State)
  if ($State.exists) { $Key.SetValue('Path', [string] $State.value, [Microsoft.Win32.RegistryValueKind] $State.kind) }
  else { $Key.DeleteValue('Path', $false) }
}

function Get-CrerUserPathState {
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
  try { return Get-CrerRegistryPathState $key }
  finally { if ($null -ne $key) { $key.Dispose() } }
}

function Set-CrerUserPathState {
  param([object] $State)
  $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
  try { Set-CrerRegistryPathState $key $State }
  finally { $key.Dispose() }
  # Explorer needs the normal environment notification to launch a terminal with the new PATH.
  # A failed notification must not disguise a successfully saved PATH as an installation failure.
  try {
    if (-not ('Crer.InstallerEnvironment' -as [type])) {
      Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Crer {
  public static class InstallerEnvironment {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
      string lParam, uint flags, uint timeout, out UIntPtr result);
    public static void Notify() {
      UIntPtr result;
      SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 2, 1000, out result);
    }
  }
}
'@
    }
    [Crer.InstallerEnvironment]::Notify()
  } catch { Write-Warning 'PATH was saved. Restart your terminal application if a new terminal cannot find crer.' }
}
