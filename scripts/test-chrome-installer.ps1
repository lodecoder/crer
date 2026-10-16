[CmdletBinding()]
param(
  [string] $ChromePath = $env:CRER_CHROME,
  [string] $Version = '152.0.7977.42',
  [switch] $Download
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'chrome-for-testing-metadata.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repoRoot ('.crer/cft-installer/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$installer = Join-Path $PSScriptRoot 'install-chrome-for-testing.ps1'
$originalChrome = $env:CRER_CHROME
$originalManifest = $env:CRER_CHROME_MANIFEST
$originalProgress = $ProgressPreference
$userChrome = [Environment]::GetEnvironmentVariable('CRER_CHROME', 'User')
$userManifest = [Environment]::GetEnvironmentVariable('CRER_CHROME_MANIFEST', 'User')
$results = [Collections.Generic.List[object]]::new()
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action, [string] $Message) {
  try { & $Action } catch { Check ($_.Exception.Message.Contains($Message)) "Unexpected diagnostic: $($_.Exception.Message)"; return }
  throw "Expected rejection containing '$Message'"
}
function Test([string] $Name, [scriptblock] $Action) {
  try { & $Action; $results.Add([pscustomobject]@{ name = $Name; passed = $true }); Write-Host "PASS $Name" }
  catch { $results.Add([pscustomobject]@{ name = $Name; passed = $false; error = $_.Exception.Message }); Write-Host "FAIL ${Name}: $($_.Exception.Message)" }
}
function Build([string] $Number) {
  return [pscustomobject]@{ version = $Number; downloads = [pscustomobject]@{ chrome = @(
    [pscustomobject]@{ platform = 'win32'; url = "https://storage.googleapis.com/chrome-for-testing-public/$Number/win32/chrome-win32.zip" },
    [pscustomobject]@{ platform = 'win64'; url = "https://storage.googleapis.com/chrome-for-testing-public/$Number/win64/chrome-win64.zip" }
  ) } }
}
try {
  $ProgressPreference = 'SilentlyContinue'
  $channels = [pscustomobject]@{ channels = [pscustomobject]@{
    Stable = Build '150.0.1.2'; Beta = Build '151.0.2.3'; Dev = Build '152.0.3.4'; Canary = Build '153.0.4.5'
  } }
  foreach ($item in @(@('stable', '150.0.1.2'), @('BETA', '151.0.2.3'), @('dev', '152.0.3.4'), @('canary', '153.0.4.5'))) {
    Test "resolves official channel layout $($item[0]) to win64" {
      $resolved = Resolve-CrerChromeForTesting -Requested $item[0] -Channels $channels
      Check ($resolved.version -ceq $item[1] -and $resolved.url -match '/win64/chrome-win64.zip$') 'Channel/platform resolution changed'
    }
  }
  Test 'exact versions select their known-good entry rather than the newest executable' {
    $resolved = Resolve-CrerChromeForTesting -Requested '152.0.3.4' -KnownGood ([pscustomobject]@{ versions = @((Build '151.0.2.3'), (Build '152.0.3.4')) })
    Check ($resolved.version -ceq '152.0.3.4') 'Exact version changed'
  }
  Test 'unsupported selectors and missing versions are diagnosed' {
    foreach ($invalid in @('latest', '152', '152.0.3', '../152.0.3.4', 'stable;anything')) { Reject { Resolve-CrerChromeForTesting -Requested $invalid } '-Version must be' }
    Reject { Resolve-CrerChromeForTesting -Requested '999.0.0.1' -KnownGood ([pscustomobject]@{ versions = @() }) } 'not present'
  }
  Test 'unavailable win64 builds and unexpected URLs are rejected' {
    $build = Build '152.0.3.4'; $build.downloads.chrome = @($build.downloads.chrome[0])
    Reject { Resolve-CrerChromeForTesting -Requested '152.0.3.4' -KnownGood ([pscustomobject]@{ versions = @($build) }) } 'does not provide a win64'
    $build = Build '152.0.3.4'; $build.downloads.chrome[1].url = 'https://example.test/untrusted.zip'
    Reject { Resolve-CrerChromeForTesting -Requested '152.0.3.4' -KnownGood ([pscustomobject]@{ versions = @($build) }) } 'unexpected win64 download URL'
  }
  Test 'temporary move/delete bounds reject root and traversal' {
    Assert-CrerInstallChildPath -Path (Join-Path $testRoot 'own-stage') -Root $testRoot
    Reject { Assert-CrerInstallChildPath -Path $testRoot -Root $testRoot } 'escapes its InstallRoot'
    Reject { Assert-CrerInstallChildPath -Path (Join-Path $testRoot '../outside') -Root $testRoot } 'escapes its InstallRoot'
  }
  Test 'ChromePath registration preserves user environment with NoPersist and records the requested selector' {
    Check (-not [string]::IsNullOrWhiteSpace($ChromePath)) 'Specify an already-installed CfT ChromePath for registration validation.'
    $registration = Join-Path $testRoot 'registration'
    & $installer -InstallRoot $registration -ChromePath $ChromePath -Version stable -NoPersist
    $manifest = Get-Content -LiteralPath (Join-Path $registration 'crer-chrome.json') -Raw | ConvertFrom-Json
    Check ($manifest.requested -ceq 'stable') 'Requested selector was overwritten by the installed product version'
    Check ($manifest.chrome -eq [IO.Path]::GetFullPath($ChromePath)) 'Registered executable changed'
    Check ($manifest.sha256 -ceq (Get-FileHash -LiteralPath $ChromePath -Algorithm SHA256).Hash.ToLowerInvariant()) 'Manifest SHA-256 changed'
    Check ($env:CRER_CHROME -eq $manifest.chrome -and $env:CRER_CHROME_MANIFEST -eq (Join-Path $registration 'crer-chrome.json')) 'Current process registration missing'
    Check ([Environment]::GetEnvironmentVariable('CRER_CHROME', 'User') -ceq $userChrome) 'NoPersist changed the user Chrome environment'
    Check ([Environment]::GetEnvironmentVariable('CRER_CHROME_MANIFEST', 'User') -ceq $userManifest) 'NoPersist changed the user manifest environment'
  }
  Test 'live official metadata resolves all four channels and the pinned exact version' {
    $liveChannels = Invoke-RestMethod -Uri 'https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json' -TimeoutSec 30
    $live = foreach ($channel in @('stable', 'beta', 'dev', 'canary')) { Resolve-CrerChromeForTesting -Requested $channel -Channels $liveChannels }
    $live += Resolve-CrerChromeForTesting -Requested $Version
    [IO.File]::WriteAllText((Join-Path $testRoot 'resolved.json'), ($live | ConvertTo-Json -Depth 10))
    Check ($live.Count -eq 5 -and $live[-1].version -ceq $Version) 'Live metadata did not resolve the required versions'
  }
  if ($Download) {
    Test 'fresh official download expands win64 Chrome and retains the same-version installation' {
      $downloadRoot = Join-Path $testRoot 'download'
      & $installer -InstallRoot $downloadRoot -Version $Version -NoPersist
      $manifest = Get-Content -LiteralPath (Join-Path $downloadRoot 'crer-chrome.json') -Raw | ConvertFrom-Json
      Check ($manifest.installed -ceq $Version) 'Downloaded binary does not match the exact requested version'
      Check ($manifest.chrome -eq (Join-Path $downloadRoot "chrome/win64-$Version/chrome-win64/chrome.exe")) 'Installation layout changed'
      Check ((Get-Item -LiteralPath $manifest.chrome).Length -gt 100000) 'Downloaded Chrome executable is empty or truncated'
      $stamp = (Get-Item -LiteralPath $manifest.chrome).LastWriteTimeUtc
      $digest = $manifest.sha256
      & $installer -InstallRoot $downloadRoot -Version $Version -NoPersist
      $again = Get-Content -LiteralPath (Join-Path $downloadRoot 'crer-chrome.json') -Raw | ConvertFrom-Json
      Check ((Get-Item -LiteralPath $again.chrome).LastWriteTimeUtc -eq $stamp -and $again.sha256 -ceq $digest) 'Existing installation was overwritten'
      Check (@(Get-ChildItem -LiteralPath $downloadRoot -Force | Where-Object { $_.Name -like '.chrome-download-*' -or $_.Name -like '.chrome-extract-*' }).Count -eq 0) 'Owned temporary download/extraction files were retained'
      Check ([Environment]::GetEnvironmentVariable('CRER_CHROME', 'User') -ceq $userChrome -and [Environment]::GetEnvironmentVariable('CRER_CHROME_MANIFEST', 'User') -ceq $userManifest) 'Download with NoPersist changed user environment'
    }
  }
} finally {
  $env:CRER_CHROME = $originalChrome
  $env:CRER_CHROME_MANIFEST = $originalManifest
  $ProgressPreference = $originalProgress
  [IO.File]::WriteAllText((Join-Path $testRoot 'summary.json'), ($results | ConvertTo-Json -Depth 10))
}
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { throw "Chrome installer validation failed: $testRoot" }
Write-Output "Chrome installer validation passed: $testRoot"
