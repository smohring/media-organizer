<#
.SYNOPSIS
    Creates a self-signed code signing certificate for testing the release
    pipeline before a real certificate exists.

.DESCRIPTION
    What this buys you: the signing step runs, signtool produces a real
    signature, and the installer and executable carry one. That is enough to
    prove the pipeline works end to end.

    What it does NOT buy you: trust. The certificate chains to nothing, so
    Windows SmartScreen still warns, the UAC prompt still says "Unknown
    publisher", and MediaOrganizer's own update check will reject the installer
    if UpdateExpectedSigner is set. Do not ship a build signed with this.

    To make the local machine trust it for testing, the certificate must also be
    imported into the Trusted Root store, which requires administrator rights.
    That is a real change to this machine's trust decisions -- pass
    -TrustOnThisMachine only if you understand that, and remove it afterwards.

.PARAMETER Subject
    Certificate subject. Must match AppSettings.UpdateExpectedSigner if that is
    configured.

.PARAMETER OutputPath
    Where to write the .pfx.

.PARAMETER Password
    Password for the .pfx.

.PARAMETER TrustOnThisMachine
    Also import the certificate into the local machine's Trusted Root store.
    Requires an elevated shell.
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=Media Organizer Test Certificate',
    [string] $OutputPath = "$PSScriptRoot\..\artifacts\test-certificate.pfx",
    [Parameter(Mandatory)] [securestring] $Password,
    [switch] $TrustOnThisMachine
)

$ErrorActionPreference = 'Stop'

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

Write-Host "Creating self-signed code signing certificate: $Subject"
$certificate = New-SelfSignedCertificate `
    -Subject $Subject `
    -Type CodeSigningCert `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears(3)

Export-PfxCertificate -Cert $certificate -FilePath $OutputPath -Password $Password | Out-Null

Write-Host ''
Write-Host "Thumbprint: $($certificate.Thumbprint)"
Write-Host "PFX:        $OutputPath"

if ($TrustOnThisMachine) {
    # Deliberately loud: this teaches the machine to trust anything signed with
    # this key, including anything else that gets hold of the .pfx.
    Write-Warning 'Importing into Trusted Root -- this machine will now trust every signature made with this key.'
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($certificate)
    $store.Close()
    Write-Host 'Imported. Remove it afterwards with:'
    Write-Host "  Remove-Item Cert:\LocalMachine\Root\$($certificate.Thumbprint)"
}

Write-Host ''
Write-Host 'To use it for a test release, set these before running release.ps1:'
Write-Host "  `$env:MEDIAORGANIZER_CERT_PATH = '$([System.IO.Path]::GetFullPath($OutputPath))'"
Write-Host "  `$env:MEDIAORGANIZER_CERT_PASSWORD = '<the password you just chose>'"
