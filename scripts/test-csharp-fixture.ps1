[CmdletBinding()]
param(
  [string] $CrerExe = 'dist/csharp/win-x64/crer.exe',
  [string] $Chrome = $env:CRER_CHROME,
  [string] $FixtureServerExe = 'csharp/Crer.FixtureServer/bin/Debug/net10.0/Crer.FixtureServer.exe',
  [string] $Deno = 'deno'
)

# Run in the same desktop/network context as CfT. Port 8080 must be free:
# this harness owns its BCL server and uses the repository YAML without rewriting it.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Absolute([string] $Path) {
  if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
  return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
foreach ($path in @($CrerExe, $Chrome, $FixtureServerExe)) {
  Check (-not [string]::IsNullOrWhiteSpace($path)) 'Specify Chrome for Testing with -Chrome or CRER_CHROME.'
  Check (Test-Path -LiteralPath (Absolute $path) -PathType Leaf) "Required executable missing: $path"
}
$CrerExe = Absolute $CrerExe
$Chrome = Absolute $Chrome
$FixtureServerExe = Absolute $FixtureServerExe
$denoCommand = (Get-Command $Deno -CommandType Application -ErrorAction Stop).Source
$scenario = Join-Path $projectRoot 'fixtures/playback/click-only.crer.yaml'
$scenarioHash = (Get-FileHash -LiteralPath $scenario -Algorithm SHA256).Hash
$testRoot = Join-Path $projectRoot ('.crer/csharp-fixture/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$summary = [Collections.Generic.List[object]]::new()
$server = $null

# Observation only: no cursor or physical-input mutation API is imported.
if (-not ('CrerCursorObservation' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CrerCursorObservation {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetCursorPos(out Point point);
    public static string Read() {
        if (!GetCursorPos(out var p)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return p.X + "," + p.Y;
    }
}
'@
}

function Run-Playback([string] $Label, [bool] $DenoImplementation, [bool] $Keep, [bool] $ChromeFlag) {
  $control = [Collections.Generic.List[string]]::new()
  $controlWatch = [Diagnostics.Stopwatch]::StartNew()
  while ($controlWatch.ElapsedMilliseconds -lt 1000) {
    $control.Add([CrerCursorObservation]::Read())
    Start-Sleep -Milliseconds 20
  }
  $baseline = $control[0]
  $controlChanged = @($control | Where-Object { $_ -ne $baseline }).Count -gt 0
  $info = [Diagnostics.ProcessStartInfo]::new($(if ($DenoImplementation) { $denoCommand } else { $CrerExe }))
  $info.WorkingDirectory = $projectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $arguments = [Collections.Generic.List[string]]::new()
  if ($DenoImplementation) { $arguments.Add('run'); $arguments.Add('-A'); $arguments.Add('src/main.ts') }
  $arguments.Add('play'); $arguments.Add($scenario)
  if ($ChromeFlag) { $arguments.Add('--chrome'); $arguments.Add($Chrome) }
  if ($Keep) { $arguments.Add('--keep-artifacts') }
  foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
  # Explicit flag must override a deliberately invalid environment path.
  $info.Environment['CRER_CHROME'] = $(if ($ChromeFlag) { Join-Path $testRoot 'missing-chrome.exe' } else { $Chrome })
  $process = [Diagnostics.Process]::Start($info)
  $outTask = $process.StandardOutput.ReadToEndAsync()
  $errTask = $process.StandardError.ReadToEndAsync()
  $samples = [Collections.Generic.List[object]]::new()
  $watch = [Diagnostics.Stopwatch]::StartNew()
  try {
    while (-not $process.HasExited) {
      $samples.Add([pscustomobject]@{ ms = $watch.ElapsedMilliseconds; position = [CrerCursorObservation]::Read() })
      Check ($watch.Elapsed.TotalSeconds -lt 60) "$Label timed out after 60 seconds"
      Start-Sleep -Milliseconds 20
    }
    $samples.Add([pscustomobject]@{ ms = $watch.ElapsedMilliseconds; position = [CrerCursorObservation]::Read() })
    $out = $outTask.GetAwaiter().GetResult()
    $err = $errTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stdout.log"), $out)
    [IO.File]::WriteAllText((Join-Path $testRoot "$Label.stderr.log"), $err)
    Check ($process.ExitCode -eq 0) "$Label returned code $($process.ExitCode): $err"
    $result = $out | ConvertFrom-Json
    Check ($result.code -eq 0 -and $result.failures.Count -eq 0) "$Label JSON result failed"
    $run = Absolute $result.runDir
    foreach ($file in @('run.json', 'steps.ndjson', 'display.json', 'before.png', 'result.png')) {
      Check (Test-Path -LiteralPath (Join-Path $run $file) -PathType Leaf) "$Label artifact missing: $file"
    }
    foreach ($png in @('before.png', 'result.png')) {
      $bytes = [IO.File]::ReadAllBytes((Join-Path $run $png))
      Check ($bytes.Length -gt 8 -and [Convert]::ToHexString($bytes[0..7]) -eq '89504E470D0A1A0A') "$Label invalid PNG: $png"
    }
    Check ((Get-FileHash -LiteralPath (Join-Path $run 'before.png')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $run 'result.png')).Hash) "$Label screenshots show no change"
    $steps = @(Get-Content -LiteralPath (Join-Path $run 'steps.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
    Check ($steps.Count -eq 7) "$Label expected seven fixture steps"
    Check (@($steps | Where-Object { $_.status -ne 'ok' }).Count -eq 0) "$Label has unsuccessful steps"
    Check (@($steps | Where-Object { $_.do -eq 'assert' }).Count -eq 2) "$Label did not verify both page states"
    $profile = Join-Path $run 'profile'
    Check ((Test-Path -LiteralPath $profile -PathType Container) -eq $Keep) "$Label profile retention differs from --keep-artifacts"
    if (-not $DenoImplementation) {
      $metadata = Get-Content -LiteralPath (Join-Path $run 'run.json') -Raw | ConvertFrom-Json
      Check ($metadata.code -eq 0 -and $metadata.failures.Count -eq 0) "$Label run.json failed"
      Check ((Absolute $metadata.chrome) -eq $Chrome) "$Label selected the wrong Chrome executable"
      $cleanup = Get-Content -LiteralPath (Join-Path $run 'cleanup.json') -Raw | ConvertFrom-Json
      Check ($cleanup.chromeExited -eq $true -and $cleanup.pendingCdpRequests -eq 0) "$Label Chrome/CDP cleanup failed"
      Check ($cleanup.errors.Count -eq 0) "$Label cleanup errors: $($cleanup.errors -join ', ')"
      Check ($cleanup.profileRetained -eq $Keep -and $cleanup.keepArtifacts -eq $Keep) "$Label cleanup profile evidence differs"
      Check ($null -eq (Get-Process -Id $metadata.chromePid -ErrorAction SilentlyContinue)) "$Label own Chrome PID is still running"
    }
    $playbackChanged = @($samples | Where-Object { $_.position -ne $control[-1] }).Count -gt 0
    $cursorStatus = if ($controlChanged -or $playbackChanged) { 'inconclusive-external-input-possible' } else { 'pass-no-physical-cursor-change-observed' }
    $observation = [pscustomobject]@{ status = $cursorStatus; controlChanged = $controlChanged; playbackChanged = $playbackChanged; control = @($control); playback = @($samples) }
    $observation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot "$Label.cursor.json")
    if ($controlChanged -or $playbackChanged) { Write-Warning "INCONCLUSIVE $Label cursor moved; user/external input cannot be isolated. Rerun without operating the mouse." }
    else { Write-Host "PASS $Label observed no physical cursor change ($baseline)" }
    $summary.Add([pscustomobject]@{ name = $Label; code = 0; runDir = $run; scenarioSha256 = $scenarioHash; keepArtifacts = $Keep; chromeSelection = $(if ($ChromeFlag) { 'flag-over-invalid-env' } else { 'environment' }); cursor = $cursorStatus })
    Write-Host "PASS $Label assertions, screenshots, profile cleanup; artifacts: $run"
  } finally {
    if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
    $process.Dispose()
  }
}

try {
  $serverOut = Join-Path $testRoot 'server.stdout.log'
  $serverErr = Join-Path $testRoot 'server.stderr.log'
  $fixtureRoot = Join-Path $projectRoot 'fixtures/playback'
  $server = Start-Process -FilePath $FixtureServerExe -ArgumentList @('--port', '8080', '--root', ('"' + $fixtureRoot + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  $ready = $false
  do {
    Check (-not $server.HasExited) "Owned fixture server exited: $(Get-Content -LiteralPath $serverErr -Raw)"
    try { $ready = (Invoke-WebRequest -Uri 'http://127.0.0.1:8080/click-only.html' -NoProxy -TimeoutSec 1).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 100 }
  } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
  Check $ready 'BCL fixture server did not become ready on 127.0.0.1:8080'
  Check (-not $server.HasExited) 'Owned server exited; refusing to test against another server'
  Run-Playback 'csharp-flag-keep' $false $true $true
  Run-Playback 'csharp-environment-dispose' $false $false $false
  Run-Playback 'deno-identical-yaml' $true $true $true
  Check ((Get-FileHash -LiteralPath $scenario -Algorithm SHA256).Hash -eq $scenarioHash) 'Fixture YAML was modified during comparison'
} finally {
  if ($server) {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force; $null = $server.WaitForExit(5000) }
    $server.Dispose()
  }
  $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'summary.json')
  Write-Host "Fixture comparison evidence: $testRoot"
}
