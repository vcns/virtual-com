<#
.SYNOPSIS
    Signs a built driver package: the driver DLL, then a freshly generated catalog that covers the signed DLL.

.PARAMETER Thumbprint
    SHA-1 thumbprint of a code-signing certificate in the current user's or local machine's "My" store.

.PARAMETER TimestampUrl
    RFC 3161 time-stamping service, so signatures stay valid after the certificate expires. Pass '' to skip.
#>
param(
    [Parameter(Mandatory)] [string] $Thumbprint,
    [string] $Configuration = 'Release',
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$inf2cat = (& "$PSScriptRoot\Kit.ps1").Inf2Cat
$package = "$root\out\$Configuration\package"

& "$PSScriptRoot\Sign-Files.ps1" "$package\vcnsvcom.dll" -Thumbprint $Thumbprint -TimestampUrl $TimestampUrl
Remove-Item "$package\vcnsvcom.cat" -ErrorAction SilentlyContinue
& $inf2cat /driver:$package /os:10_RS5_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) { throw 'Catalog generation failed.' }
& "$PSScriptRoot\Sign-Files.ps1" "$package\vcnsvcom.cat" -Thumbprint $Thumbprint -TimestampUrl $TimestampUrl

# Informational: a test certificate shows as untrusted until it is added to the machine's trusted stores.
Get-AuthenticodeSignature "$package\vcnsvcom.dll", "$package\vcnsvcom.cat" |
    ForEach-Object { '{0}: {1} ({2})' -f (Split-Path $_.Path -Leaf), $_.SignerCertificate.Subject, $_.Status }
