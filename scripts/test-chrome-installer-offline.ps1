[CmdletBinding()]
param(
  [switch] $Worker,
  [string] $FixtureArchive,
  [string] $FixtureExecutable,
  [string] $FixtureVersion,
  [string] $TestRoot
)

$ErrorActionPreference = 'Stop'
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
$installer = Join-Path $PSScriptRoot 'install-chrome-for-testing.ps1'

if ($Worker) {
  # ZIP を作る親プロセスとは分け、圧縮用アセンブリを先に読み込まない新規セッションで検証する。
  $zipInitiallyLoaded = $null -ne ('System.IO.Compression.ZipFile' -as [type])
  if ($PSVersionTable.PSVersion.Major -eq 5) { Check (-not $zipInitiallyLoaded) 'Windows PowerShell fixture must start without ZipFile loaded.' }
  $userChrome = [Environment]::GetEnvironmentVariable('CRER_CHROME', 'User')
  $userManifest = [Environment]::GetEnvironmentVariable('CRER_CHROME_MANIFEST', 'User')
  $downloadUrl = "https://storage.googleapis.com/chrome-for-testing-public/$FixtureVersion/win64/chrome-win64.zip"
  $fixtureState = [pscustomobject]@{ downloads = 0; metadataReads = 0; basicParsing = $false }
  function Invoke-RestMethod {
    param([string] $Uri, [int] $TimeoutSec)
    Check ($Uri -ceq 'https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json') 'Unexpected metadata request.'
    $fixtureState.metadataReads++
    return [pscustomobject]@{ channels = [pscustomobject]@{ Stable = [pscustomobject]@{
      version = $FixtureVersion; downloads = [pscustomobject]@{ chrome = @(
        [pscustomobject]@{ platform = 'win64'; url = $downloadUrl }
      ) }
    } } }
  }
  function Invoke-WebRequest {
    param([string] $Uri, [string] $OutFile, [int] $TimeoutSec, [switch] $UseBasicParsing)
    Check ($Uri -ceq $downloadUrl -and $TimeoutSec -eq 600) 'Unexpected archive request.'
    $fixtureState.downloads++
    $fixtureState.basicParsing = $UseBasicParsing.IsPresent
    Copy-Item -LiteralPath $FixtureArchive -Destination $OutFile
  }
  # 通信だけを差し替え、ZIP の事前検査、展開、実行ファイルの版・ハッシュ検査は本体を通す。
  $installRoot = Join-Path $TestRoot 'installed'
  & $installer -InstallRoot $installRoot -Version stable -NoPersist
  $manifestPath = Join-Path $installRoot 'crer-chrome.json'
  $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  $expectedExe = Join-Path $installRoot "chrome/win64-$FixtureVersion/chrome-win64/chrome.exe"
  Check ($manifest.chrome -eq $expectedExe -and (Test-Path -LiteralPath $expectedExe -PathType Leaf)) 'Fresh ZIP was not installed in the expected directory.'
  Check ($manifest.requested -ceq 'stable' -and $manifest.installed -ceq $FixtureVersion) 'Installed product version or requested selector changed.'
  $digest = (Get-FileHash -LiteralPath $FixtureExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
  Check ($manifest.sha256 -ceq $digest -and (Get-FileHash -LiteralPath $expectedExe -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $digest) 'Archive extraction or manifest digest changed.'
  Check ($fixtureState.basicParsing) 'Archive download must use basic parsing for Windows PowerShell compatibility.'
  $stamp = (Get-Item -LiteralPath $expectedExe).LastWriteTimeUtc
  & $installer -InstallRoot $installRoot -Version stable -NoPersist
  Check ($fixtureState.downloads -eq 1 -and $fixtureState.metadataReads -eq 2) 'Same-version reuse downloaded or skipped metadata unexpectedly.'
  Check ((Get-Item -LiteralPath $expectedExe).LastWriteTimeUtc -eq $stamp) 'Same-version installation was overwritten.'
  Check (@(Get-ChildItem -LiteralPath $installRoot -Force | Where-Object { $_.Name -like '.chrome-download-*' -or $_.Name -like '.chrome-extract-*' }).Count -eq 0) 'Temporary download or extraction files remain.'
  Check ($env:CRER_CHROME -eq $expectedExe -and $env:CRER_CHROME_MANIFEST -eq $manifestPath) 'Current process registration is missing.'
  Check ([Environment]::GetEnvironmentVariable('CRER_CHROME', 'User') -ceq $userChrome -and [Environment]::GetEnvironmentVariable('CRER_CHROME_MANIFEST', 'User') -ceq $userManifest) 'NoPersist changed the user environment.'
  [pscustomobject]@{ powershell = $PSVersionTable.PSVersion.ToString(); zipInitiallyLoaded = $zipInitiallyLoaded; passed = $true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestRoot 'result.json') -Encoding UTF8
  Write-Output "PASS offline Chrome ZIP installation: PowerShell $($PSVersionTable.PSVersion)"
  return
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testDirectory = Join-Path $repoRoot ('.crer/cft-installer-offline/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
# Windows 標準の実行ファイルを複製し、版リソースまで本物の小さなオフライン ZIP を作る。実行はしない。
$fixtureExe = Join-Path $env:SystemRoot 'System32/cmd.exe'
$payload = Join-Path $testDirectory 'payload/chrome-win64'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $payload 'chrome.exe')
# System32 の MUI から補われる版情報を避け、ZIP に入る複製自身のリソースを読む。
$fixtureExe = Join-Path $payload 'chrome.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixtureExe).ProductVersion
Check ($version -match '^\d+\.\d+\.\d+\.\d+$') 'Fixture executable needs a four-part product version.'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $testDirectory 'chrome-win64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path -Parent $payload), $archive)
$shells = @(
  [pscustomobject]@{ name = 'powershell7'; path = (Get-Command pwsh.exe -ErrorAction Stop).Source; major = 7 },
  [pscustomobject]@{ name = 'windows-powershell51'; path = (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'); major = 5 }
)
$results = @()
foreach ($shell in $shells) {
  $sessionRoot = Join-Path $testDirectory $shell.name
  New-Item -ItemType Directory -Path $sessionRoot | Out-Null
  & $shell.path -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -Worker `
    -FixtureArchive $archive -FixtureExecutable $fixtureExe -FixtureVersion $version -TestRoot $sessionRoot
  if ($LASTEXITCODE -ne 0) { throw "Offline Chrome ZIP validation failed in $($shell.name): $sessionRoot" }
  $result = Get-Content -LiteralPath (Join-Path $sessionRoot 'result.json') -Raw | ConvertFrom-Json
  Check (([version] $result.powershell).Major -eq $shell.major) 'Child session used the wrong PowerShell edition.'
  $results += $result
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testDirectory 'summary.json') -Encoding UTF8
Write-Output "Chrome installer offline validation passed: $testDirectory"
