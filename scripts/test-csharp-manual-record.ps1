[CmdletBinding()]
param(
  [string] $CrerExe = 'dist/win-x64/crer.exe',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $FixtureServerExe = 'tests/Crer.FixtureServer/bin/Release/net10.0/Crer.FixtureServer.exe',
  [int] $ManualTimeoutSeconds = 900
)

# A human supplies the marker and Increment clicks. No physical input is synthesized.
# The caller writes the emitted stopFile after the user confirms the two clicks.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Absolute([string] $Path) {
  if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
  return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
foreach ($path in @($CrerExe, $Chrome)) {
  Check (-not [string]::IsNullOrWhiteSpace($path)) 'Specify the crer and Chrome executables.'
  Check (Test-Path -LiteralPath (Absolute $path) -PathType Leaf) "Required executable missing: $path"
}
if (-not $PSBoundParameters.ContainsKey('FixtureServerExe') -and -not (Test-Path -LiteralPath (Absolute $FixtureServerExe) -PathType Leaf)) {
  & dotnet build (Join-Path $projectRoot 'tests/Crer.FixtureServer/Crer.FixtureServer.csproj') -c Release --nologo | Out-Host
  Check ($LASTEXITCODE -eq 0) 'Building the BCL fixture server failed.'
}
Check (-not [string]::IsNullOrWhiteSpace($FixtureServerExe)) 'Specify the BCL fixture server executable.'
Check (Test-Path -LiteralPath (Absolute $FixtureServerExe) -PathType Leaf) "Required executable missing: $FixtureServerExe"
Check ($ManualTimeoutSeconds -gt 0) 'ManualTimeoutSeconds must be positive.'
$CrerExe = Absolute $CrerExe
$Chrome = Absolute $Chrome
$FixtureServerExe = Absolute $FixtureServerExe
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $projectRoot ('.crer/manual-record/' + $runId)
$null = New-Item -ItemType Directory -Path $testRoot -Force
$raw = Join-Path $testRoot 'manual.ndjson'
$stopFile = Join-Path $testRoot 'stop'
$normalized = Join-Path $testRoot 'manual.normalized.crer.yaml'
$asserted = Join-Path $testRoot 'manual.asserted.crer.yaml'
$statePath = Join-Path $testRoot 'state.json'
$summaryPath = Join-Path $testRoot 'summary.json'
$startedAt = [DateTimeOffset]::UtcNow.ToString('o')
$state = [ordered]@{
  stage = 'starting'; startedAt = $startedAt; testRoot = $testRoot; raw = $raw; stopFile = $stopFile
  normalized = $normalized; asserted = $asserted; statePath = $statePath
  recordPid = $null; serverPid = $null; chromePids = @(); url = $null; readyAt = $null
  instructions = 'Click the magenta marker once, then click Increment exactly once, then tell Codex you are done. Codex writes stopFile.'
}
$results = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{ status = 'running'; startedAt = $startedAt; completedAt = $null; manualPhysicalInput = $true; results = $results; error = $null; playRunDir = $null }
$server = $null
$record = $null
$outTask = $null
$errTask = $null
$ownedChrome = [Collections.Generic.List[object]]::new()
function Save-State {
  $temporary = $statePath + '.tmp'
  [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 12))
  [IO.File]::Move($temporary, $statePath, $true)
}
function Save-Summary { [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12)) }
function Start-Owned([string] $Executable, [string[]] $Arguments) {
  $info = [Diagnostics.ProcessStartInfo]::new($Executable)
  $info.WorkingDirectory = $projectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
  return [Diagnostics.Process]::Start($info)
}
function Invoke-Crer([string] $Label, [string[]] $Arguments) {
  $process = Start-Owned $CrerExe $Arguments
  $outTask = $process.StandardOutput.ReadToEndAsync()
  $errTask = $process.StandardError.ReadToEndAsync()
  try {
    Check ($process.WaitForExit(90000)) "$Label exceeded 90 seconds"
    $outText = $outTask.GetAwaiter().GetResult()
    $errText = $errTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stdout.log"), $outText)
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stderr.log"), $errText)
    $results.Add([pscustomobject]@{ name = $Label; code = $process.ExitCode; completedAt = [DateTimeOffset]::UtcNow.ToString('o') })
    Check ($process.ExitCode -eq 0) "$Label returned $($process.ExitCode): $errText"
    return $outText
  } finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
  }
}

try {
  Save-State
  Save-Summary
  $server = Start-Owned $FixtureServerExe @('--port', '0')
  $state.serverPid = $server.Id
  $lineTask = $server.StandardOutput.ReadLineAsync()
  Check ($lineTask.Wait(10000)) 'BCL fixture server startup timed out'
  $line = $lineTask.Result
  Check ($line -match 'http://127\.0\.0\.1:(\d+)/') "Unexpected fixture server startup: $line"
  $port = [int] $Matches[1]
  $url = "http://127.0.0.1:$port/click-only.html"
  $response = Invoke-WebRequest -Uri $url -TimeoutSec 5
  Check ($response.StatusCode -eq 200) 'BCL fixture server is unreachable in this execution context'
  $state.url = $url
  $record = Start-Owned $CrerExe @('record', $raw, '--chrome', $Chrome, '--url', $url,
    '--stop-file', $stopFile, '--content-size', '800,600', '--position', '1200,80', '--name', '手動記録受入')
  $state.recordPid = $record.Id
  $state.stage = 'preparing-marker'
  Save-State
  Write-Output "Manual record state: $statePath"
  Write-Output "Stop file: $stopFile"
  $outTask = $record.StandardOutput.ReadToEndAsync()
  $errTask = $record.StandardError.ReadToEndAsync()
  $startup = [Diagnostics.Stopwatch]::StartNew()
  while (-not (Test-Path -LiteralPath $raw) -and -not $record.HasExited -and $startup.Elapsed.TotalSeconds -lt 30) {
    Start-Sleep -Milliseconds 50
  }
  if ($record.HasExited) {
    [IO.File]::WriteAllText((Join-Path $testRoot 'record.stdout.log'), $outTask.GetAwaiter().GetResult())
    $errText = $errTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $testRoot 'record.stderr.log'), $errText)
    throw "Recorder exited before manual interaction (code $($record.ExitCode)): $errText"
  }
  Check (Test-Path -LiteralPath $raw) 'Recorder did not reach its native input loop'
  $state.chromePids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($record.Id)" | Where-Object { $_.Name -eq 'chrome.exe' } | ForEach-Object { [int] $_.ProcessId })
  foreach ($ownedPid in $state.chromePids) {
    $ownedProcess = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    if ($ownedProcess) {
      $ownedChrome.Add([pscustomobject]@{ id = $ownedPid; startTicks = $ownedProcess.StartTime.ToUniversalTime().Ticks })
      $ownedProcess.Dispose()
    }
  }
  $state.stage = 'awaiting-manual-clicks'
  $state.readyAt = [DateTimeOffset]::UtcNow.ToString('o')
  Save-State
  Write-Output 'READY: click the magenta marker once, then Increment exactly once, then reply done. Codex will create the stop file.'
  $manualWatch = [Diagnostics.Stopwatch]::StartNew()
  while (-not $record.HasExited -and -not (Test-Path -LiteralPath $stopFile)) {
    Check ($manualWatch.Elapsed.TotalSeconds -lt $ManualTimeoutSeconds) "Manual interaction exceeded $ManualTimeoutSeconds seconds"
    Start-Sleep -Milliseconds 100
  }
  Check ($record.WaitForExit(10000)) 'Recorder did not stop after the stop file appeared'
  [IO.File]::WriteAllText((Join-Path $testRoot 'record.stdout.log'), $outTask.GetAwaiter().GetResult())
  $errText = $errTask.GetAwaiter().GetResult()
  [IO.File]::WriteAllText((Join-Path $testRoot 'record.stderr.log'), $errText)
  $results.Add([pscustomobject]@{ name = 'manual-record'; code = $record.ExitCode; completedAt = [DateTimeOffset]::UtcNow.ToString('o') })
  Check ($record.ExitCode -eq 0) "Manual recording failed: $errText"
  Check (Test-Path -LiteralPath $stopFile) 'Recording ended before the user confirmation stop file appeared'
  $metadata = Get-Content -LiteralPath ($raw + '.meta.json') -Raw | ConvertFrom-Json
  Check (-not $metadata.incomplete) 'Manual recording was marked incomplete'
  Check ($null -ne $metadata.marker_calibration) 'No physical calibration click was recorded'
  Check ((Get-Item -LiteralPath $raw).Length -gt 0) 'No physical input was captured after calibration'
  $rawEvents = @(Get-Content -LiteralPath $raw | Where-Object { $_.Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
  Check (@($rawEvents | Where-Object { $_.kind -eq 2 }).Count -eq 1 -and @($rawEvents | Where-Object { $_.kind -eq 3 }).Count -eq 1) 'Expected exactly one complete physical click after the calibration marker'
  foreach ($ownedPid in $state.chromePids) {
    Check ($null -eq (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue)) "Owned recording Chrome remains alive: $ownedPid"
  }
  $state.stage = 'normalizing-and-replaying'
  Save-State
  $null = Invoke-Crer 'normalize' @('normalize', $raw, '--output', $normalized, '--url', $url, '--name', '手動記録再生受入')
  $null = Invoke-Crer 'validate-normalized' @('validate', $normalized)
  Copy-Item -LiteralPath $normalized -Destination $asserted
  [IO.File]::AppendAllText($asserted, '  - { do: assert, state: complete, locator_hint: { role: status, name: Result, text: "Clicks: 1" } }' + [Environment]::NewLine)
  $null = Invoke-Crer 'validate-asserted' @('validate', $asserted)
  $playText = Invoke-Crer 'play' @('play', $asserted, '--chrome', $Chrome)
  $play = $playText | ConvertFrom-Json
  Check ($play.code -eq 0 -and $play.failures.Count -eq 0) 'Replayed recording returned a failure result'
  $run = Absolute $play.runDir
  $steps = @(Get-Content -LiteralPath (Join-Path $run 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
  Check ($steps.Count -gt 0 -and $steps[-1].do -eq 'assert' -and $steps[-1].status -eq 'ok') 'Final Clicks: 1 assertion did not succeed'
  $cleanup = Get-Content -LiteralPath (Join-Path $run 'cleanup.json') -Raw | ConvertFrom-Json
  Check ($cleanup.chromeExited -eq $true -and $cleanup.pendingCdpRequests -eq 0 -and $cleanup.errors.Count -eq 0) 'Replay cleanup left Chrome, pending CDP requests or errors'
  $summary.playRunDir = $run
  $summary.status = 'passed'
  $summary.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
  $state.stage = 'passed'
  Save-State
  Save-Summary
  Write-Output "Manual record -> normalize -> validate -> play acceptance passed: $testRoot"
} catch {
  $summary.status = 'failed'
  $summary.error = $_.Exception.Message
  $summary.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
  $state.stage = 'failed'
  Save-State
  Save-Summary
  throw
} finally {
  if ($record) {
    if (-not $record.HasExited) { $record.Kill($true); $record.WaitForExit() }
    if ($outTask) { [IO.File]::WriteAllText((Join-Path $testRoot 'record.stdout.log'), $outTask.GetAwaiter().GetResult()) }
    if ($errTask) { [IO.File]::WriteAllText((Join-Path $testRoot 'record.stderr.log'), $errTask.GetAwaiter().GetResult()) }
    $record.Dispose()
  }
  foreach ($identity in $ownedChrome) {
    $remaining = Get-Process -Id $identity.id -ErrorAction SilentlyContinue
    if ($remaining) {
      try {
        if ($remaining.StartTime.ToUniversalTime().Ticks -eq $identity.startTicks -and -not $remaining.HasExited) {
          $remaining.Kill($true)
          $remaining.WaitForExit()
        }
      } finally { $remaining.Dispose() }
    }
  }
  if ($server) { if (-not $server.HasExited) { $server.Kill($true); $server.WaitForExit() }; $server.Dispose() }
}
