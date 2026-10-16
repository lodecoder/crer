[CmdletBinding()]
param([string] $SourceDirectory = 'dist/win-x64', [switch] $RegistryTests)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'install-common.ps1')
$source = if ([IO.Path]::IsPathRooted($SourceDirectory)) { [IO.Path]::GetFullPath($SourceDirectory) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $SourceDirectory)) }
$testRoot = Join-Path $projectRoot ('.crer/install-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$processPath = $env:Path
$chrome = $env:CRER_CHROME
$chromeManifest = $env:CRER_CHROME_MANIFEST
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action, [string] $Message) {
  try { & $Action } catch {
    Check ($_.Exception.Message -like "*$Message*") "Unexpected error: $($_.Exception.Message)"
    return
  }
  throw "Expected rejection: $Message"
}
function Test([string] $Name, [scriptblock] $Action) {
  try { & $Action; $results.Add([ordered]@{ name = $Name; passed = $true }); Write-Host "PASS $Name" }
  catch { $results.Add([ordered]@{ name = $Name; passed = $false; error = $_.Exception.Message }); Write-Host "FAIL ${Name}: $($_.Exception.Message)" }
}
function Extract([string] $Zip, [string] $Destination) { Expand-Archive -LiteralPath $Zip -DestinationPath $Destination }
try {
  $packageRoot = Join-Path $testRoot 'package'
  & (Join-Path $PSScriptRoot 'package-release.ps1') -Version 'v0.0.0-install-test' -SourceDirectory $source -OutputDirectory $packageRoot
  $archive = Join-Path $packageRoot 'crer-win-x64.zip'
  $payload = Join-Path $testRoot 'extracted'
  Extract $archive $payload
  $destination = Join-Path $testRoot ('installed space-' + [char]0x65e5 + [char]0x672c)
  Test 'package contains release files and a matching SHA256SUMS' {
    foreach ($name in (Get-CrerReleaseFiles)) { Check (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf) "Missing $name" }
    $line = [IO.File]::ReadAllText((Join-Path $packageRoot 'SHA256SUMS')).Trim()
    Check ($line -ceq ((Get-CrerFileDigest $archive) + '  crer-win-x64.zip')) 'Archive checksum mismatch'
    Check ((Get-CrerFileDigest (Join-Path $payload 'crer.exe')) -ceq (Get-CrerFileDigest (Join-Path $source 'crer.exe'))) 'Packaging modified the public executable'
    Reject { & (Join-Path $PSScriptRoot 'package-release.ps1') -Version '../invalid' -SourceDirectory $source -OutputDirectory (Join-Path $testRoot 'bad-package') } 'Version'
    Reject { & (Join-Path $PSScriptRoot 'package-release.ps1') -Version 'v0.0.0-install-test' -SourceDirectory $source -OutputDirectory $packageRoot } 'exist'
  }
  Test 'install in a path with spaces without modifying user PATH or Chrome settings' {
    & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath
    $state = Read-CrerInstallState $destination
    Check ($state.version -ceq 'v0.0.0-install-test' -and -not $state.pathAdded) 'Wrong installation state'
    foreach ($name in (Get-CrerReleaseFiles)) {
      Check (@($state.files | Where-Object { $_.name -ceq $name }).Count -eq 1) "Release file ownership is missing: $name"
    }
    Check ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $userPath) 'User PATH changed'
    Check ($env:CRER_CHROME -ceq $chrome -and $env:CRER_CHROME_MANIFEST -ceq $chromeManifest) 'Chrome settings changed'
    & (Join-Path $destination 'crer.exe') help | Out-Null
    Check ($LASTEXITCODE -eq 0) 'Installed Native AOT executable did not run'
  }
  Test 'reinstall and update retain untracked files and installation identity' {
    [IO.File]::WriteAllText((Join-Path $destination 'notes.txt'), 'user notes')
    & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath
    $releasePath = Join-Path $payload 'release.json'
    $release = Get-Content -LiteralPath $releasePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $release.version = 'v0.0.1-install-test'
    [IO.File]::WriteAllText($releasePath, ($release | ConvertTo-Json))
    & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath
    Check ((Read-CrerInstallState $destination).version -ceq 'v0.0.1-install-test') 'Version was not updated'
    Check ([IO.File]::ReadAllText((Join-Path $destination 'notes.txt')) -ceq 'user notes') 'User notes changed'
    Reject { & (Join-Path $destination 'install.ps1') -InstallDirectory $destination -NoPath } 'separate folder'
  }
  Test 'updates reject modified installed files before writing any files' {
    $readme = Join-Path $destination 'README.en.md'
    $original = [IO.File]::ReadAllBytes($readme)
    [IO.File]::AppendAllText($readme, 'user change')
    $stateHash = Get-CrerFileDigest (Join-Path $destination '.crer-install.json')
    Reject { & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath } 'was modified'
    Check ((Get-CrerFileDigest (Join-Path $destination '.crer-install.json')) -ceq $stateHash) 'State changed after refusal'
    Check ([IO.File]::ReadAllText($readme).EndsWith('user change')) 'Modified file changed'
    [IO.File]::WriteAllBytes($readme, $original)
  }
  Test 'updates accept a previous release state and retain ownership of its retired DLL' {
    $legacyDll = Join-Path $destination 'crer-win-input.dll'
    [IO.File]::WriteAllText($legacyDll, 'retired release file')
    $statePath = Join-Path $destination '.crer-install.json'
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $state.files += [pscustomobject]@{ name = 'crer-win-input.dll'; sha256 = Get-CrerFileDigest $legacyDll }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 6))
    & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath
    Check ([IO.File]::ReadAllText($legacyDll) -ceq 'retired release file') 'Retired owned file changed during update'
    Check (@((Read-CrerInstallState $destination).files | Where-Object { $_.name -ceq 'crer-win-input.dll' }).Count -eq 1) 'Retired file ownership was lost'
    Check (-not (Test-Path -LiteralPath (Join-Path $payload 'crer-win-input.dll'))) 'New release still ships the retired DLL'
  }
  Test 'a locked release file rolls back an earlier replacement and preserves installation state' {
    $binary = Join-Path $payload 'crer.exe'
    $original = [IO.File]::ReadAllBytes($binary)
    $changedBytes = [byte[]]::new($original.Length + 1)
    [Array]::Copy($original, $changedBytes, $original.Length)
    [IO.File]::WriteAllBytes($binary, $changedBytes)
    $stateHash = Get-CrerFileDigest (Join-Path $destination '.crer-install.json')
    $binaryHash = Get-CrerFileDigest (Join-Path $destination 'crer.exe')
    $locked = [IO.File]::Open((Join-Path $destination 'README.en.md'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Reject { & (Join-Path $payload 'install.ps1') -InstallDirectory $destination -NoPath } 'process' }
    finally { $locked.Dispose(); [IO.File]::WriteAllBytes($binary, $original) }
    Check ((Get-CrerFileDigest (Join-Path $destination 'crer.exe')) -ceq $binaryHash) 'Earlier binary replacement was not rolled back'
    Check ((Get-CrerFileDigest (Join-Path $destination '.crer-install.json')) -ceq $stateHash) 'State changed after rollback'
    Check (@(Get-ChildItem -LiteralPath $destination -Directory -Force | Where-Object { $_.Name -like '.install-*' }).Count -eq 0) 'Successful rollback retained staging'
  }
  Test 'unmanaged directories and incomplete releases are rejected without deletion' {
    $foreign = Join-Path $testRoot 'foreign'
    New-Item -ItemType Directory -Path $foreign | Out-Null
    [IO.File]::WriteAllText((Join-Path $foreign 'keep.txt'), 'keep')
    Reject { & (Join-Path $payload 'install.ps1') -InstallDirectory $foreign -NoPath } 'not managed'
    Reject { & (Join-Path $payload 'uninstall.ps1') -InstallDirectory $foreign } 'No managed'
    Check ([IO.File]::ReadAllText((Join-Path $foreign 'keep.txt')) -ceq 'keep') 'Unmanaged data changed'
    $missing = Join-Path $testRoot 'incomplete'
    New-Item -ItemType Directory -Path $missing | Out-Null
    Reject { & (Join-Path $payload 'install.ps1') -SourceDirectory $missing -InstallDirectory (Join-Path $testRoot 'not-created') -NoPath } 'Release file missing'
    Check (-not (Test-Path -LiteralPath (Join-Path $testRoot 'not-created'))) 'Incomplete source created an install directory'
  }
  Test 'state file traversal is rejected before deleting any file' {
    $statePath = Join-Path $destination '.crer-install.json'
    $original = [IO.File]::ReadAllBytes($statePath)
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $state.files[0].name = '..\outside.txt'
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 6))
    try {
      Reject { & (Join-Path $payload 'uninstall.ps1') -InstallDirectory $destination } 'invalid'
      Check (Test-Path -LiteralPath (Join-Path $destination 'crer.exe')) 'Traversal validation deleted a file'
    } finally { [IO.File]::WriteAllBytes($statePath, $original) }
  }
  Test 'junction installation paths are rejected without changing the target' {
    $outside = Join-Path $testRoot 'junction-target'
    $link = Join-Path $testRoot 'junction'
    New-Item -ItemType Directory -Path $outside | Out-Null
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    try {
      Reject { & (Join-Path $payload 'install.ps1') -InstallDirectory (Join-Path $link 'child') -NoPath } 'junction'
      Check (-not (Test-Path -LiteralPath (Join-Path $outside 'child'))) 'Junction target changed'
    } finally { [IO.Directory]::Delete($link) }
  }
  Test 'PATH comparison and removal preserve unrelated and preexisting entries' {
    $value = 'C:\Other;;"' + $destination.ToUpperInvariant() + '\";C:\Tools'
    Check (Test-CrerPathContains $value $destination) 'Quoted/case-insensitive PATH entry was missed'
    Check ((Remove-CrerPathEntry $value $destination) -ceq 'C:\Other;;C:\Tools') 'Other PATH entries changed'
    Check (-not (Test-CrerPathContains ($destination + '-other') $destination)) 'Prefix confused with exact PATH entry'
  }
  Test 'uninstall preserves modified files, retry scripts, and user notes' {
    [IO.File]::AppendAllText((Join-Path $destination 'README.en.md'), 'user change')
    & (Join-Path $destination 'uninstall.ps1')
    Check (Test-Path -LiteralPath (Join-Path $destination 'README.en.md')) 'Modified README removed'
    Check (Test-Path -LiteralPath (Join-Path $destination 'uninstall.ps1')) 'Retry script removed'
    Check (Test-Path -LiteralPath (Join-Path $destination 'notes.txt')) 'User notes removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $destination 'crer.exe'))) 'Tracked executable retained'
    Check (-not (Test-Path -LiteralPath (Join-Path $destination 'crer-win-input.dll'))) 'Retired owned DLL was not removed'
    # Restoring this owned file lets a second invocation finish without recursive directory deletion.
    Copy-Item -LiteralPath (Join-Path $payload 'README.en.md') -Destination (Join-Path $destination 'README.en.md') -Force
    & (Join-Path $destination 'uninstall.ps1')
    Check (@(Get-ChildItem -LiteralPath $destination -Force).Count -eq 1) 'Unexpected files retained after retry'
    Check ([IO.File]::ReadAllText((Join-Path $destination 'notes.txt')) -ceq 'user notes') 'Untracked file changed'
  }
  Test 'clean uninstall removes only its empty installation directory' {
    $clean = Join-Path $testRoot 'clean'
    $env:Path = $processPath + ';' + $clean
    $expectedPath = $env:Path
    & (Join-Path $payload 'install.ps1') -InstallDirectory $clean -NoPath
    & (Join-Path $clean 'uninstall.ps1')
    Check (-not (Test-Path -LiteralPath $clean)) 'Empty installation directory remained'
    Check (Test-Path -LiteralPath $payload) 'Release source removed'
    Check ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $userPath) 'NoPath test changed user PATH'
    Check ($env:Path -ceq $expectedPath) 'Uninstall removed a preexisting PATH entry'
  }
  Test 'uninstall retains its retry scripts when a tracked file is locked' {
    $lockedInstall = Join-Path $testRoot 'locked-uninstall'
    & (Join-Path $payload 'install.ps1') -InstallDirectory $lockedInstall -NoPath
    $locked = [IO.File]::Open((Join-Path $lockedInstall 'README.ja.md'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Reject { & (Join-Path $lockedInstall 'uninstall.ps1') } 'retry uninstall.ps1' }
    finally { $locked.Dispose() }
    Check (Test-Path -LiteralPath (Join-Path $lockedInstall 'uninstall.ps1')) 'Retry script was deleted'
    Check (Test-Path -LiteralPath (Join-Path $lockedInstall 'install-common.ps1')) 'Retry helper was deleted'
    & (Join-Path $lockedInstall 'uninstall.ps1')
    Check (-not (Test-Path -LiteralPath $lockedInstall)) 'Retry did not complete'
  }
  if ($RegistryTests) {
    Test 'default PATH install and uninstall preserve raw variables, value kind and concurrent changes' {
      # Redirect only the registry storage location in a test payload; production PATH is never written.
      $subkey = 'Software\crer-install-tests\' + [Guid]::NewGuid().ToString('N')
      $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subkey)
      try {
        $raw = '%USERPROFILE%\tools;;C:\Other;'
        $key.SetValue('Path', $raw, [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $isolatedPayload = Join-Path $testRoot 'registry-payload'
        New-Item -ItemType Directory -Path $isolatedPayload | Out-Null
        foreach ($file in (Get-ChildItem -LiteralPath $payload -File)) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $isolatedPayload $file.Name) }
        $helper = Join-Path $isolatedPayload 'install-common.ps1'
        $text = [IO.File]::ReadAllText($helper).Replace("OpenSubKey('Environment')", "OpenSubKey('$subkey')").Replace("CreateSubKey('Environment')", "CreateSubKey('$subkey')")
        [IO.File]::WriteAllText($helper, $text)
        $registryInstall = Join-Path $testRoot 'registry-install'
        & (Join-Path $isolatedPayload 'install.ps1') -InstallDirectory $registryInstall
        $state = Read-CrerInstallState $registryInstall
        Check ($state.pathAdded -and -not $state.pathCreated) 'Added PATH ownership was not tracked'
        $saved = Get-CrerRegistryPathState $key
        Check ($saved.value -ceq ($raw + ';' + $registryInstall)) 'Raw PATH variables or empty entries changed'
        Check ($saved.kind -eq [Microsoft.Win32.RegistryValueKind]::ExpandString) 'ExpandString value kind changed'
        & (Join-Path $isolatedPayload 'install.ps1') -InstallDirectory $registryInstall
        Check ((Get-CrerRegistryPathState $key).value -ceq $saved.value) 'Reinstall duplicated PATH'
        $key.SetValue('Path', $saved.value + ';C:\AddedLater', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        & (Join-Path $registryInstall 'uninstall.ps1')
        $after = Get-CrerRegistryPathState $key
        Check ($after.value -ceq ($raw + ';C:\AddedLater') -and $after.kind -eq $saved.kind) 'Uninstall changed another PATH entry or value kind'
        $key.DeleteValue('Path')
        & (Join-Path $isolatedPayload 'install.ps1') -InstallDirectory $registryInstall
        Check ((Read-CrerInstallState $registryInstall).pathCreated) 'Absent PATH creation not tracked'
        & (Join-Path $registryInstall 'uninstall.ps1')
        Check (-not (Get-CrerRegistryPathState $key).exists) 'Uninstall did not restore absent PATH value'
        $key.SetValue('Path', $registryInstall, [Microsoft.Win32.RegistryValueKind]::String)
        & (Join-Path $isolatedPayload 'install.ps1') -InstallDirectory $registryInstall
        Check (-not (Read-CrerInstallState $registryInstall).pathAdded) 'Preexisting PATH was claimed'
        & (Join-Path $registryInstall 'uninstall.ps1')
        $after = Get-CrerRegistryPathState $key
        Check ($after.value -ceq $registryInstall -and $after.kind -eq [Microsoft.Win32.RegistryValueKind]::String) 'Preexisting String PATH was changed'
        Check ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $userPath) 'Real user PATH changed'
      } finally {
        $key.Dispose()
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($subkey, $false)
        $env:Path = $processPath
      }
    }
  }
} finally {
  $env:Path = $processPath
  [IO.File]::WriteAllText((Join-Path $testRoot 'summary.json'), ($results | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
  Write-Host "Evidence: $testRoot"
}
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Installer tests failed; see summary.json.' }
