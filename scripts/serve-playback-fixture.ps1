#requires -Version 7.2
[CmdletBinding()]
param([int] $Port = 8080)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet run --project (Join-Path $projectRoot 'tests/Crer.FixtureServer/Crer.FixtureServer.csproj') -c Release -- --port $Port --root (Join-Path $projectRoot 'fixtures/playback')
if ($LASTEXITCODE -ne 0) { throw "BCL fixture server failed ($LASTEXITCODE)." }
