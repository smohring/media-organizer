<#
.SYNOPSIS
    Produces the shippable application directory under artifacts\publish.

.DESCRIPTION
    Self-contained win-x64 build: the .NET 10 runtime travels inside the folder,
    so the target machine needs no runtime installed and no other application
    can break this one by replacing a shared runtime underneath it.

    The settings live in src\MediaOrganizer.App\Properties\PublishProfiles\win-x64.pubxml
    rather than in this script, so that publishing from Visual Studio produces
    exactly the same output as publishing from here.

.PARAMETER Clean
    Delete the output directory first. Use this before a release: a stale file
    from an earlier layout would otherwise be packaged along with the rest.
#>
[CmdletBinding()]
param(
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\MediaOrganizer.App\MediaOrganizer.App.csproj'
$output = Join-Path $repo 'artifacts\publish'

if ($Clean -and (Test-Path $output)) {
    Write-Host "Removing $output"
    Remove-Item $output -Recurse -Force
}

Write-Host 'Publishing self-contained win-x64 build ...'
dotnet publish $project -p:PublishProfile=win-x64 --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $output 'MediaOrganizer.exe'
if (-not (Test-Path $exe)) {
    throw "Publish finished but $exe is missing. Did AssemblyName change?"
}

# The version is read back out of the produced binary rather than from the
# .props file, so the installer and the update feed can never disagree with what
# was actually built.
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$size = [math]::Round(((Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ''
Write-Host "Version:   $version"
Write-Host "Output:    $output"
Write-Host "Size:      $size MB"

# Written to the pipeline so release.ps1 can pick it up.
[pscustomobject]@{
    Version   = $version
    Directory = $output
    Executable = $exe
}
