[CmdletBinding()]
param([int] $Port = 8080)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Get-Command deno -ErrorAction SilentlyContinue)) {
  throw 'deno was not found.'
}

$code = @"
Deno.serve({ port: $Port }, async (request) => {
  const path = new URL(request.url).pathname;
  if (path !== '/' && path !== '/index.html') return new Response('Not found', { status: 404 });
  return new Response(await Deno.readFile('fixtures/playback/index.html'), { headers: { 'content-type': 'text/html; charset=utf-8' } });
});
"@

Write-Host "Serving playback fixture at http://127.0.0.1:$Port/index.html"
Push-Location $projectRoot
try { & deno eval $code } finally { Pop-Location }
