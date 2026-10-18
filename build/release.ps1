<#
.SYNOPSIS
    Builds a complete release: portable archive, checksum and update feed.

.DESCRIPTION
    Media Organizer ships portable — a self-contained folder that is unpacked
    anywhere and started from there. There is no installer, so a release is
    three artifacts:

        artifacts\MediaOrganizer-<version>-win-x64.zip   the download
        artifacts\MediaOrganizer-<version>-win-x64.sha256  its checksum
        artifacts\latest.json                            the update feed

    Steps, in order:

      1. Publish self-contained win-x64 (carries the .NET 10 runtime).
      2. Sign MediaOrganizer.exe, if a certificate is configured. Signing
         happens BEFORE packing — the archive must contain the signed binary,
         because that is the file the updater verifies after unpacking.
      3. Pack the publish directory. Its contents sit at the root of the
         archive, so unpacking it over an installation replaces it exactly.
      4. Hash the archive and write latest.json.

    Publishing the result is a manual step on purpose (see -WhatToDoNext).

.PARAMETER Notes
    Release notes shown to users in the update panel.

.PARAMETER BaseUrl
    Where the archive will be reachable. The default matches a GitHub release
    for the tag v<version>.

.PARAMETER RequireSignature
    Fail if the build cannot be signed. Use this for a real release.

.PARAMETER SkipPublish
    Reuse an existing artifacts\publish directory. For iterating on the
    packaging steps without waiting for a full publish.
#>
[CmdletBinding()]
param(
    [string] $Notes = '',
    [string] $BaseUrl = 'https://github.com/smohring/media-organizer/releases/download',
    [switch] $RequireSignature,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

# ---------------------------------------------------------------- 1. publish

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'MediaOrganizer.exe'))) {
        throw 'SkipPublish was given but artifacts\publish holds no build.'
    }
    Write-Host 'Reusing existing publish output.'
}
else {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Clean | Out-Null
}

$exe = Join-Path $publishDir 'MediaOrganizer.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
if (-not $version) {
    throw "Could not read a version from $exe."
}

Write-Host ''
Write-Host "Releasing version $version"

# ------------------------------------------------------------------- 2. sign
# Only our own assemblies are signed. The runtime files come from Microsoft and
# already carry their signatures; re-signing them would be both pointless and
# wrong.

$ourBinaries = @(
    $exe,
    (Join-Path $publishDir 'MediaOrganizer.Core.dll'),
    (Join-Path $publishDir 'MediaOrganizer.dll')
) | Where-Object { Test-Path $_ }

& (Join-Path $PSScriptRoot 'sign.ps1') -Path $ourBinaries -Require:$RequireSignature

# ------------------------------------------------------------------- 3. pack

$archiveName = "MediaOrganizer-$version-win-x64.zip"
$archivePath = Join-Path $artifacts $archiveName
if (Test-Path $archivePath) { Remove-Item $archivePath -Force }

Write-Host "Packing $archiveName ..."

# Compress-Archive on the directory itself would put everything under a
# "publish" folder inside the zip. The wildcard packs the contents instead, so
# the archive root IS the application directory.
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $archivePath -CompressionLevel Optimal

# ------------------------------------------------------------------- 4. feed

$hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $archivePath).Length

"$hash *$archiveName" | Set-Content -Path (Join-Path $artifacts "$archiveName.sha256") -Encoding ascii

# Built as an ordered hashtable so the field order in the file matches the
# documented example; ConvertTo-Json keeps insertion order.
$feed = [ordered]@{
    version    = $version
    released   = (Get-Date -Format 'yyyy-MM-dd')
    notes      = $Notes
    packageUrl = "$BaseUrl/v$version/$archiveName"
    sha256     = $hash
    sizeBytes  = $size
}

$feedPath = Join-Path $artifacts 'latest.json'

# Written through .NET rather than Set-Content -Encoding utf8: in PowerShell 5.1
# that switch emits a BOM, and a BOM in a file served as application/json trips
# up a fair number of clients.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($feedPath, ($feed | ConvertTo-Json), $utf8NoBom)

Write-Host ''
Write-Host 'Done.'
Write-Host "  Archive:  $archivePath  ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "  SHA-256:  $hash"
Write-Host "  Feed:     $feedPath"
Write-Host ''
Write-Host 'What to do next:'
Write-Host "  1. Create the GitHub release for tag v$version and attach $archiveName."
Write-Host "  2. Copy $feedPath over release\latest.json in the repository and push it."
Write-Host '     Running clients read that file; until it is pushed, nobody is offered the update.'
