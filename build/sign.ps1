<#
.SYNOPSIS
    Applies an Authenticode signature to one or more files.

.DESCRIPTION
    Wraps signtool.exe. Nothing about the certificate is stored in this
    repository; the script reads it from the environment:

        MEDIAORGANIZER_CERT_PATH        path to a .pfx file
        MEDIAORGANIZER_CERT_PASSWORD    its password
                       -- or --
        MEDIAORGANIZER_CERT_THUMBPRINT  a certificate already in the store,
                                        which is how hardware tokens are used

    A timestamp is always requested. Without one, every signature made with a
    certificate stops validating the day that certificate expires — including
    the signatures on releases that shipped years earlier.

    If no certificate is configured the script says so and exits with 0. That is
    deliberate: an unsigned local build is a normal state, and a release script
    that dies here would block ordinary work. Pass -Require to turn a missing
    certificate into an error for a real release.

.PARAMETER Path
    Files to sign.

.PARAMETER Require
    Fail when no certificate is configured or signing does not succeed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [string[]] $Path,

    [switch] $Require
)

begin {
    $ErrorActionPreference = 'Stop'
    $files = @()

    function Find-SignTool {
        # 1. Already on PATH.
        $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($onPath) { return $onPath.Source }

        # 2. Windows SDK. Several versions may be installed side by side; the
        #    newest x64 build is the one to use.
        $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        if (Test-Path $kits) {
            $candidate = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($candidate) { return $candidate.FullName }
        }

        # 3. The NuGet package that carries the same tool, restored by anyone who
        #    ran the build once. Cheaper than installing the whole SDK.
        $nuget = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
        if (Test-Path $nuget) {
            $candidate = Get-ChildItem $nuget -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($candidate) { return $candidate.FullName }
        }

        return $null
    }
}

process {
    $files += $Path
}

end {
    $certPath = $env:MEDIAORGANIZER_CERT_PATH
    $thumbprint = $env:MEDIAORGANIZER_CERT_THUMBPRINT

    if (-not $certPath -and -not $thumbprint) {
        $message = 'No code signing certificate configured (MEDIAORGANIZER_CERT_PATH or ' +
                   'MEDIAORGANIZER_CERT_THUMBPRINT). Files are left unsigned.'
        if ($Require) { throw $message }
        Write-Warning $message
        return
    }

    $signtool = Find-SignTool
    if (-not $signtool) {
        # The NuGet route is listed first because it needs no installation and
        # no elevation -- it drops the same tool into the package cache, which
        # Find-SignTool searches.
        $message = @'
signtool.exe was not found. Either of these gets it:

  dotnet restore build\signtool\signtool.csproj
  winget install --id Microsoft.WindowsSDK.10.0.26100 --exact
'@
        if ($Require) { throw $message }
        Write-Warning $message
        return
    }

    # RFC 3161 timestamping with SHA-256. The older /t Authenticode timestamp
    # protocol is still accepted by Windows but is SHA-1 based.
    $timestampUrl = 'http://timestamp.digicert.com'

    foreach ($file in $files) {
        if (-not (Test-Path $file)) { throw "Cannot sign missing file: $file" }

        $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestampUrl, '/v')

        if ($thumbprint) {
            $arguments += @('/sha1', $thumbprint)
        }
        else {
            $arguments += @('/f', $certPath)
            if ($env:MEDIAORGANIZER_CERT_PASSWORD) {
                $arguments += @('/p', $env:MEDIAORGANIZER_CERT_PASSWORD)
            }
        }

        $arguments += $file

        Write-Host "Signing $file"
        & $signtool @arguments
        if ($LASTEXITCODE -ne 0) {
            $message = "signtool failed for $file with exit code $LASTEXITCODE."
            if ($Require) { throw $message }
            Write-Warning $message
        }
    }
}
