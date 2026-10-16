# Shared helpers for C# fixture acceptance. Dot-source from a fixture script.
$script:FixtureProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Resolve-FixturePath([string] $Path) {
  if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
  return [IO.Path]::GetFullPath((Join-Path $script:FixtureProjectRoot $Path))
}
function Read-CrerResult([string] $Text) {
  $starts = [regex]::Matches($Text, '(?m)^[\{\[]')
  for ($index = $starts.Count - 1; $index -ge 0; $index--) {
    try { return ,($Text.Substring($starts[$index].Index) | ConvertFrom-Json -ErrorAction Stop) } catch { }
  }
  throw 'CLI output did not end with a JSON result object.'
}
function Start-OwnedFixtureServer([int] $Port = 8080, [string] $Executable) {
  if ([string]::IsNullOrWhiteSpace($Executable)) {
    & dotnet build (Join-Path $script:FixtureProjectRoot 'tests/Crer.FixtureServer/Crer.FixtureServer.csproj') -c Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Fixture server build failed ($LASTEXITCODE)." }
    $Executable = 'tests/Crer.FixtureServer/bin/Release/net10.0/Crer.FixtureServer.exe'
  }
  $root = Join-Path $script:FixtureProjectRoot ('.crer/fixture/' + [Guid]::NewGuid().ToString('N'))
  $null = New-Item -ItemType Directory -Path $root -Force
  $info = [Diagnostics.ProcessStartInfo]::new((Resolve-FixturePath $Executable))
  $info.WorkingDirectory = $script:FixtureProjectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($argument in @('--port', "$Port", '--root', (Join-Path $script:FixtureProjectRoot 'fixtures/playback'))) { $info.ArgumentList.Add($argument) }
  $process = [Diagnostics.Process]::Start($info)
  $errors = $process.StandardError.ReadToEndAsync()
  try {
    $ready = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    if (-not $ready -or $process.HasExited) { throw 'Owned BCL fixture server did not bind its listener.' }
    $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/index.html" -NoProxy -TimeoutSec 3
    if ($response.StatusCode -ne 200 -or $process.HasExited) { throw 'Owned BCL fixture server was not reachable.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    return @{ Process = $process; Root = $root; Stdout = $stdout; Stderr = $errors; Ready = $ready }
  } catch {
    if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
    [IO.File]::WriteAllText((Join-Path $root 'server.stderr.log'), $errors.GetAwaiter().GetResult())
    $process.Dispose()
    throw
  }
}
function Stop-OwnedFixtureServer($Server) {
  if (-not $Server) { return }
  try {
    if (-not $Server.Process.HasExited) { $Server.Process.Kill($true); $null = $Server.Process.WaitForExit(5000) }
    [IO.File]::WriteAllText((Join-Path $Server.Root 'server.stdout.log'), $Server.Ready + "`n" + $Server.Stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $Server.Root 'server.stderr.log'), $Server.Stderr.GetAwaiter().GetResult())
  } finally { $Server.Process.Dispose() }
}
function Invoke-FixtureCli([string] $Executable, [string[]] $Arguments, [int] $ExpectedCode, [string] $Evidence, [string] $Label,
    [Collections.IDictionary] $Environment = @{}, [int] $TimeoutSeconds = 120) {
  $info = [Diagnostics.ProcessStartInfo]::new((Resolve-FixturePath $Executable))
  $info.WorkingDirectory = $script:FixtureProjectRoot
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
  foreach ($name in $Environment.Keys) { $info.Environment[$name] = $Environment[$name] }
  $process = [Diagnostics.Process]::Start($info)
  $stdout = $process.StandardOutput.ReadToEndAsync()
  $stderr = $process.StandardError.ReadToEndAsync()
  try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "$Label timed out after $TimeoutSeconds seconds." }
    $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $Evidence "$Label.stdout.log"), $out)
    [IO.File]::WriteAllText((Join-Path $Evidence "$Label.stderr.log"), $err)
    if ($process.ExitCode -ne $ExpectedCode) { throw "$Label returned $($process.ExitCode), expected ${ExpectedCode}: $err" }
    $result = Read-CrerResult $out
    if ($result -isnot [array] -and $result.code -ne $ExpectedCode) { throw "$Label JSON code differs from process exit code." }
    return ,$result
  } finally {
    if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
    try { [IO.File]::WriteAllText((Join-Path $Evidence "$Label.stdout.log"), $stdout.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()) } catch { }
    try { [IO.File]::WriteAllText((Join-Path $Evidence "$Label.stderr.log"), $stderr.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()) } catch { }
    $process.Dispose()
  }
}
