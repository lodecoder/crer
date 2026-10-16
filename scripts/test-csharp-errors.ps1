#requires -Version 7.2
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $CrerExe,
  [string] $Chrome = $env:CRER_CHROME,
  [string] $StartupStub = 'tests\Crer.StartupStub\bin\Release\net10.0\Crer.StartupStub.exe',
  [string] $InterruptHelper = 'tests\Crer.InterruptHelper\bin\Release\net10.0\Crer.InterruptHelper.exe',
  [int] $Port = 8080
)

# Requires the externally started BCL fixture server. Does not start/stop a shared server.
# Build test helpers first:
# dotnet build tests/Crer.StartupStub -c Release
# dotnet build tests/Crer.InterruptHelper -c Release
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fixture-common.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Absolute([string] $Path) {
  if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
  return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
foreach ($path in @($CrerExe, $Chrome, $StartupStub, $InterruptHelper)) {
  if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath (Absolute $path) -PathType Leaf)) {
    throw "Required executable missing: $path"
  }
}
$CrerExe = Absolute $CrerExe
$Chrome = Absolute $Chrome
$StartupStub = Absolute $StartupStub
$InterruptHelper = Absolute $InterruptHelper
$null = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/click-only.html" -TimeoutSec 3
$testRoot = Join-Path $projectRoot ('.crer\csharp-errors\' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$base = [IO.File]::ReadAllText((Join-Path $projectRoot 'fixtures\playback\click-only.crer.yaml'))
$base = $base.Replace('http://127.0.0.1:8080/', "http://127.0.0.1:$Port/")
$summary = [Collections.Generic.List[object]]::new()

function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function New-Scenario([string] $Label, [string] $Steps = '', [string] $Extra = '') {
  $name = 'errors-' + $Label + '-' + [Guid]::NewGuid().ToString('N')
  $yaml = [regex]::Replace($base, '(?m)^name:.*$', "name: $name")
  if ($Steps) { $yaml = [regex]::Replace($yaml, '(?ms)^steps:.*\z', "steps:`n$Steps`n") }
  if ($Extra) { $yaml = $yaml.Replace('playback:', "playback:`n  $Extra") }
  $file = Join-Path $testRoot "$name.crer.yaml"
  [IO.File]::WriteAllText($file, $yaml)
  return @{ Name = $name; File = $file }
}
function Find-Run($Case) {
  $runs = Join-Path $projectRoot '.crer\runs'
  if (-not (Test-Path -LiteralPath $runs)) { return $null }
  # These generated case names are ASCII; the public run ID retains at most 64 runes.
  $artifactName = $Case.Name.Substring(0, [Math]::Min(64, $Case.Name.Length))
  return Get-ChildItem -LiteralPath $runs -Directory | Where-Object { $_.Name -like "*-$artifactName-*" } | Select-Object -First 1
}
function Start-Cli([string[]] $Arguments, [bool] $RemoveChromeEnvironment = $false, [hashtable] $EnvironmentOverrides = @{}) {
  $info = [Diagnostics.ProcessStartInfo]::new($CrerExe)
  $info.WorkingDirectory = $projectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
  if ($RemoveChromeEnvironment) { $null = $info.Environment.Remove('CRER_CHROME') }
  foreach ($key in $EnvironmentOverrides.Keys) { $info.Environment[$key] = [string]$EnvironmentOverrides[$key] }
  $process = [Diagnostics.Process]::Start($info)
  return @{ Process = $process; Stdout = $process.StandardOutput.ReadToEndAsync(); Stderr = $process.StandardError.ReadToEndAsync() }
}
function Finish-Cli($Started, $Case, [int] $Expected, [string] $Diagnostic, [bool] $NoRun = $false) {
  try {
    Check ($Started.Process.WaitForExit(45000)) "CLI did not exit within 45 seconds for $($Case.Name)"
    $out = $Started.Stdout.GetAwaiter().GetResult()
    $err = $Started.Stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $testRoot "$($Case.Name).stdout.log"), $out)
    [IO.File]::WriteAllText((Join-Path $testRoot "$($Case.Name).stderr.log"), $err)
    Check ($Started.Process.ExitCode -eq $Expected) "Expected code $Expected, got $($Started.Process.ExitCode): $err"
    Check (($out + $err).Contains($Diagnostic)) "Missing diagnostic '$Diagnostic': $out $err"
    Check (-not ($err -match 'Unhandled exception|\bat Crer\..*line \d+')) "Unhandled stack trace: $err"
    $run = Find-Run $Case
    if ($NoRun) { Check ($null -eq $run) 'Invalid input created a run / launched Chrome' }
    else {
      Check ($null -ne $run) 'Playback did not create artifacts'
      $result = Read-CrerResult $out
      Check ($result.code -eq $Expected) 'JSON result disagrees with process exit code'
      Check ($result.failures.Count -gt 0) 'Error result has no failures'
      Check ((Absolute $result.runDir) -eq $run.FullName) 'JSON result points to a different run'
      Assert-Cleanup $run $Expected
    }
    $summary.Add([pscustomobject]@{ name = $Case.Name; code = $Expected; runDir = $(if ($run) { $run.FullName } else { $null }) })
    Write-Host "PASS $($Case.Name) (code $Expected)"
    return $run
  } finally {
    if (-not $Started.Process.HasExited) { $Started.Process.Kill($true); $Started.Process.WaitForExit(5000) | Out-Null }
    $Started.Process.Dispose()
  }
}
function Assert-Cleanup($Run, [int] $Expected) {
  $metadata = Get-Content -LiteralPath (Join-Path $Run.FullName 'run.json') -Raw | ConvertFrom-Json
  $cleanup = Get-Content -LiteralPath (Join-Path $Run.FullName 'cleanup.json') -Raw | ConvertFrom-Json
  Check ($metadata.code -eq $Expected) 'run.json code differs'
  Check ($cleanup.chromeExited -eq $true) 'Own Chrome was not stopped'
  Check ($cleanup.pendingCdpRequests -eq 0) 'Pending CDP requests leaked'
  Check ($cleanup.errors.Count -eq 0) "Cleanup failed: $($cleanup.errors -join ', ')"
  Check ($cleanup.profileRetained -eq $false) 'Temporary profile retained without --keep-artifacts'
  Check (-not (Test-Path -LiteralPath $metadata.profile)) 'Temporary profile exists after exit'
  if ($metadata.chromePid) { Check ($null -eq (Get-Process -Id $metadata.chromePid -ErrorAction SilentlyContinue)) 'Own Chrome PID still exists' }
}
function Wait-BeforeScreenshot($Started, $Case) {
  $deadline = [DateTime]::UtcNow.AddSeconds(20)
  while ([DateTime]::UtcNow -lt $deadline) {
    Check (-not $Started.Process.HasExited) 'CLI exited before the test trigger'
    $run = Find-Run $Case
    if ($run -and (Test-Path -LiteralPath (Join-Path $run.FullName 'before.png'))) { return $run }
    Start-Sleep -Milliseconds 50
  }
  throw 'No screenshot trigger before deadline'
}

try {
  $case = New-Scenario 'missing-environment'
  $null = Finish-Cli (Start-Cli @('play', $case.File) $true) $case 3 'must be specified' $true

  $case = New-Scenario 'duplicate-option'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome, '--chrome', $Chrome)) $case 2 'more than once' $true
  $case = New-Scenario 'missing-option-value'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome')) $case 2 'requires a value' $true
  $case = New-Scenario 'unknown-option'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome, '--migration-unknown')) $case 2 'unknown option' $true

  $case = New-Scenario 'invalid-yaml'
  [IO.File]::AppendAllText($case.File, "`nunknown_setting: true`n")
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome)) $case 2 'unknown_setting' $true
  $case = New-Scenario 'malformed-yaml'
  [IO.File]::WriteAllText($case.File, 'version: [unclosed')
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome)) $case 2 'invalid YAML' $true
  $case = New-Scenario 'unsupported-step' '  - { do: migration_unknown }'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome)) $case 2 'not supported' $true
  $case = New-Scenario 'unsupported-setting' '' 'migration_unknown: 2'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome)) $case 2 'migration_unknown' $true

  $case = New-Scenario 'missing-chrome'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', (Join-Path $testRoot 'missing-chrome.exe'))) $case 3 'does not exist'

  $case = New-Scenario 'chrome-exit-before-endpoint'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $StartupStub) $false @{ CRER_STARTUP_STUB_EXIT_CODE = '42' }) $case 3 'CfT exited during startup (42)'

  $case = New-Scenario 'assertion' '  - { do: assert, locator_hint: { role: status, name: Result, text: "Impossible result" } }'
  $run = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $Chrome)) $case 4 'assert'
  Check (Test-Path -LiteralPath (Join-Path $run.FullName 'failure-0.png')) 'Assertion failure screenshot missing'
  $steps = Get-Content -LiteralPath (Join-Path $run.FullName 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json }
  Check ($steps[0].status -eq 'failed') 'Failed assert was not recorded'

  # navigation_ms is accepted but the fixed legacy runtime does not read it.
  # The owned stub exercises the fixed CDP-endpoint deadline instead.
  $case = New-Scenario 'startup-timeout' '' 'timeouts: { navigation_ms: 300, action_ms: 1000 }'
  $null = Finish-Cli (Start-Cli @('play', $case.File, '--chrome', $StartupStub)) $case 3 'startup timed out'

  $case = New-Scenario 'cdp-disconnect' @'
  - { do: screenshot, name: before }
  - { do: sleep, ms: 60000 }
  - { do: assert, state: complete }
'@
  $started = Start-Cli @('play', $case.File, '--chrome', $Chrome)
  try {
    $run = Wait-BeforeScreenshot $started $case
    $metadata = Get-Content -LiteralPath (Join-Path $run.FullName 'run.json') -Raw | ConvertFrom-Json
    $ownChrome = Get-Process -Id $metadata.chromePid -ErrorAction Stop
    Check ([IO.Path]::GetFullPath($ownChrome.Path) -eq $Chrome) 'Refusing to stop a PID that is not this test Chrome executable'
    Check ([IO.Path]::GetFullPath($metadata.profile).StartsWith($run.FullName + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Profile is not scoped to this test run'
    Stop-Process -Id $ownChrome.Id -Force
    $null = Finish-Cli $started $case 3 'CDP'
    $started = $null
  } finally {
    if ($started) {
      try { if (-not $started.Process.HasExited) { $started.Process.Kill($true) } } catch { }
      $started.Process.Dispose()
    }
  }

  $case = New-Scenario 'ctrl-c-hold' @'
  - { do: wait_for, state: visible, locator_hint: { role: button, name: Increment } }
  - { do: screenshot, name: before }
  - { do: click, at: { x: 130, y: 64 }, hold_ms: 60000 }
'@
  $controlDir = Join-Path $testRoot 'ctrl-c'
  $evidencePath = Join-Path $testRoot 'interrupt-evidence.json'
  & $InterruptHelper $CrerExe $case.File $Chrome $projectRoot $case.Name $controlDir $evidencePath
  Check ($LASTEXITCODE -eq 0) "Interrupt helper failed; inspect $evidencePath"
  $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
  Check ($evidence.pressedObserved -eq $true -and $evidence.ctrlCGenerated -eq $true) 'Ctrl+C was not delivered during a verified primary-button hold'
  Check ($evidence.exitCode -eq 5) 'Ctrl+C did not return code 5'
  $result = Read-CrerResult (Get-Content -LiteralPath (Join-Path $controlDir 'stdout.log') -Raw)
  Check ($result.code -eq 5 -and $result.failures.Count -gt 0) 'Interrupted JSON result is invalid'
  $run = Find-Run $case
  Check ($null -ne $run) 'Interrupted run artifacts missing'
  Assert-Cleanup $run 5
  $steps = @(Get-Content -LiteralPath (Join-Path $run.FullName 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
  Check ($steps[-1].kind -eq 'interrupted') 'Held click was not recorded as interrupted'
  $mouse = @(Get-Content -LiteralPath (Join-Path $run.FullName 'cdp.ndjson') |
    ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.method -eq 'Input.dispatchMouseEvent' })
  Check ($mouse.Count -eq 3) 'Interrupted hold did not record three successful mouse messages'
  Check (($mouse.params.type -join ',') -eq 'mouseMoved,mousePressed,mouseReleased') 'Held primary button was not released before browser shutdown'
  Check ($mouse[-1].params.buttons -eq 0 -and $mouse[-1].params.clickCount -eq 1) 'Mouse release changed click semantics'
  $summary.Add([pscustomobject]@{ name = $case.Name; code = 5; runDir = $run.FullName })
  Write-Host "PASS $($case.Name) (code 5; mousePressed observed through CDP)"
} finally {
  $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot 'summary.json')
  Write-Host "Error-path test evidence: $testRoot"
}
