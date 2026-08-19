[CmdletBinding()]
param([int] $Port = 8080)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\fixtures\playback'))
if (-not (Get-Command deno -ErrorAction SilentlyContinue)) {
  throw 'deno was not found.'
}

$code = @"
const root = $(ConvertTo-Json $root);
Deno.serve({ port: $Port }, async (request) => {
  const path = new URL(request.url).pathname;
  if (path !== '/' && path !== '/index.html') return new Response('Not found', { status: 404 });
  return new Response(await Deno.readFile(root + '/index.html'), { headers: { 'content-type': 'text/html; charset=utf-8' } });
});
"@

Write-Host "Serving playback fixture at http://127.0.0.1:$Port/index.html"
& deno eval --allow-net --allow-read $code
