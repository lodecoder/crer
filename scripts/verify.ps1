[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
  & dotnet build csharp/Crer.Cli/Crer.Cli.csproj -c $Configuration --nologo -warnaserror
  if ($LASTEXITCODE -ne 0) { throw "C# build failed ($LASTEXITCODE)." }
  $tests = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'csharp') -Directory | Where-Object {
    $_.Name -like '*Tests' -and $_.Name -notlike '*IntegrationTests' -and (Test-Path -LiteralPath (Join-Path $_.FullName ($_.Name + '.csproj')) -PathType Leaf)
  } | Sort-Object Name)
  foreach ($project in $tests) {
    & dotnet run --project $project.FullName -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "$($project.Name) failed ($LASTEXITCODE)." }
  }
  & dotnet run --project native/tests/Crer.WinInput.NativeTests.csproj -c $Configuration
  if ($LASTEXITCODE -ne 0) { throw "Native tests failed ($LASTEXITCODE)." }
  Write-Host "PASS C# verify: $($tests.Count) CLI test projects and native tests."
} finally { Pop-Location }
