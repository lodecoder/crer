[CmdletBinding()]
param(
  [string] $CrerExe = 'dist/win-x64/crer.exe',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $FixtureServerExe = 'tests/Crer.FixtureServer/bin/Release/net10.0/Crer.FixtureServer.exe'
)

# Automatic lifecycle acceptance only. This harness observes physical input and never injects it.
# The interactive record -> normalize -> play acceptance remains a separate manual requirement.
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
$CrerExe = Absolute $CrerExe
$Chrome = Absolute $Chrome
$FixtureServerExe = Absolute $FixtureServerExe
$testRoot = Join-Path $projectRoot ('.crer/csharp-record/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$summary = [Collections.Generic.List[object]]::new()
$server = $null
$record = $null

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
function Invoke-Crer([string] $Label, [string[]] $Arguments, [int] $Expected = 0) {
  $process = Start-Owned $CrerExe $Arguments
  $outTask = $process.StandardOutput.ReadToEndAsync()
  $errTask = $process.StandardError.ReadToEndAsync()
  try {
    Check ($process.WaitForExit(30000)) "$Label exceeded 30 seconds"
    $outText = $outTask.GetAwaiter().GetResult()
    $errText = $errTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stdout.log"), $outText)
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stderr.log"), $errText)
    Check ($process.ExitCode -eq $Expected) "$Label returned $($process.ExitCode): $errText"
    $summary.Add([pscustomobject]@{ name = $Label; code = $process.ExitCode })
  } finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
  }
}

try {
  $server = Start-Owned $FixtureServerExe @('--port', '0')
  $lineTask = $server.StandardOutput.ReadLineAsync()
  Check ($lineTask.Wait(10000)) 'BCL fixture server startup timed out'
  $line = $lineTask.Result
  Check ($line -match 'http://127\.0\.0\.1:(\d+)/') "Unexpected fixture server startup: $line"
  $port = [int] $Matches[1]
  $url = "http://127.0.0.1:$port/click-only.html"
  $response = Invoke-WebRequest -Uri $url -TimeoutSec 5
  Check ($response.StatusCode -eq 200) 'Fixture server is unreachable in this execution context'

  $raw = Join-Path $testRoot 'timed.ndjson'
  Invoke-Crer 'timed-record' @('record', $raw, '--chrome', $Chrome, '--url', $url,
    '--duration-ms', '3000', '--content-size', '800,600', '--position', '1200,80', '--name', '記録自動受入')
  Check (Test-Path -LiteralPath $raw -PathType Leaf) 'Timed recorder did not create raw NDJSON'
  $metadata = Get-Content -LiteralPath ($raw + '.meta.json') -Raw | ConvertFrom-Json
  Check ([ulong] $metadata.qpc_frequency_hz -gt 0) 'QPC frequency missing'
  Check ($metadata.css_viewport.x -gt 0 -and $metadata.css_viewport.y -gt 0) 'Recording viewport missing'
  Check ($metadata.screen_pixels_per_css_pixel.x -eq 1 -and $metadata.screen_pixels_per_css_pixel.y -eq 1) 'Explicit screen/CSS scale missing'
  Check (-not $metadata.incomplete) 'Timed recording was marked incomplete'
  $yaml = Join-Path $testRoot 'timed.crer.yaml'
  Invoke-Crer 'normalize-record' @('normalize', $raw, '--output', $yaml, '--url', $url)
  Invoke-Crer 'validate-record' @('validate', $yaml)

  # No synthetic clicks are used. The stop-file case can finish without marker calibration
  # because specifying --duration-ms 0 preserves the established no-calibration option.
  $stopFile = Join-Path $testRoot 'stop'
  $rawStop = Join-Path $testRoot 'stop-file.ndjson'
  $record = Start-Owned $CrerExe @('record', $rawStop, '--chrome', $Chrome,
    '--url', $url, '--duration-ms', '0', '--stop-file', $stopFile, '--content-size', '800,600', '--name', 'stop-file')
  $outTask = $record.StandardOutput.ReadToEndAsync()
  $errTask = $record.StandardError.ReadToEndAsync()
  $watch = [Diagnostics.Stopwatch]::StartNew()
  while (-not (Test-Path -LiteralPath $rawStop) -and -not $record.HasExited -and $watch.Elapsed.TotalSeconds -lt 15) {
    Start-Sleep -Milliseconds 50
  }
  Check (Test-Path -LiteralPath $rawStop) 'Stop-file recorder did not start'
  [IO.File]::WriteAllText($stopFile, '')
  Check ($record.WaitForExit(10000)) 'Stop-file recorder failed to stop'
  [IO.File]::WriteAllText((Join-Path $testRoot 'stop-file.stdout.log'), $outTask.GetAwaiter().GetResult())
  $errText = $errTask.GetAwaiter().GetResult()
  [IO.File]::WriteAllText((Join-Path $testRoot 'stop-file.stderr.log'), $errText)
  Check ($record.ExitCode -eq 0) "Stop-file recording failed: $errText"
  $summary.Add([pscustomobject]@{ name = 'stop-file'; code = $record.ExitCode })
  Check (-not (Get-Content -LiteralPath ($rawStop + '.meta.json') -Raw | ConvertFrom-Json).incomplete) 'Stop-file recording incomplete'

  # Windows 入力は実行ファイルに組み込み済みで、廃止した DLL オプションは明示的に拒否する。
  Invoke-Crer 'removed-dll-option' @('record', (Join-Path $testRoot 'rejected.ndjson'), '--chrome', $Chrome,
    '--dll', 'unused.dll', '--duration-ms', '1') 2

  [IO.File]::WriteAllText((Join-Path $testRoot 'summary.json'), ($summary | ConvertTo-Json -Depth 10))
  Write-Output "C# record lifecycle acceptance passed: $testRoot"
} finally {
  if ($record) { if (-not $record.HasExited) { $record.Kill($true); $record.WaitForExit() }; $record.Dispose() }
  if ($server) { if (-not $server.HasExited) { $server.Kill($true); $server.WaitForExit() }; $server.Dispose() }
}
