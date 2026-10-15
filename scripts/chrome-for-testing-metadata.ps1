# Official CfT metadata resolution, shared by the installer and its offline validation.
# https://github.com/GoogleChromeLabs/chrome-for-testing#json-api-endpoints
function Resolve-CrerChromeForTesting {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string] $Requested,
    [object] $Channels,
    [object] $KnownGood
  )
  $baseUrl = 'https://googlechromelabs.github.io/chrome-for-testing'
  if ($Requested -match '^(stable|beta|dev|canary)$') {
    $metadataUrl = "$baseUrl/last-known-good-versions-with-downloads.json"
    if ($null -eq $Channels) { $Channels = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 30 }
    $channel = @($Channels.channels.PSObject.Properties | Where-Object { $_.Name -ieq $Requested })
    if ($channel.Count -ne 1) { throw "Chrome for Testing channel '$Requested' was not present in official metadata." }
    $build = $channel[0].Value
  } elseif ($Requested -match '^\d+\.\d+\.\d+\.\d+$') {
    $metadataUrl = "$baseUrl/known-good-versions-with-downloads.json"
    if ($null -eq $KnownGood) { $KnownGood = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60 }
    $versions = @($KnownGood.versions | Where-Object { $_.version -ceq $Requested })
    if ($versions.Count -ne 1) { throw "Chrome for Testing version '$Requested' was not present in official known-good metadata." }
    $build = $versions[0]
  } else {
    throw '-Version must be stable, beta, dev, canary, or an exact four-part version such as 152.0.7977.42.'
  }
  $resolvedVersion = [string] $build.version
  if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Official Chrome for Testing metadata did not contain a valid version.' }
  $downloads = @($build.downloads.chrome | Where-Object { $_.platform -ceq 'win64' })
  if ($downloads.Count -ne 1) { throw "Chrome for Testing $resolvedVersion does not provide a win64 chrome download." }
  $downloadUrl = [string] $downloads[0].url
  $expectedUrl = "https://storage.googleapis.com/chrome-for-testing-public/$resolvedVersion/win64/chrome-win64.zip"
  if ($downloadUrl -cne $expectedUrl) { throw "Official Chrome for Testing metadata returned an unexpected win64 download URL: $downloadUrl" }
  return [pscustomobject]@{ requested = $Requested; version = $resolvedVersion; url = $downloadUrl; metadata = $metadataUrl }
}

function Assert-CrerInstallChildPath {
  param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Root)
  $absolutePath = [IO.Path]::GetFullPath($Path)
  $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
  if (-not $absolutePath.StartsWith($absoluteRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Installer temporary path escapes its InstallRoot: $absolutePath"
  }
}
