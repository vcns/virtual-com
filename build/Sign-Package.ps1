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
$kit = & "$PSScriptRoot\Kit.ps1"
$signtool = $kit.SignTool
$inf2cat = $kit.Inf2Cat
$package = "$root\out\$Configuration\package"

function Sign([string] $file) {
    $arguments = @('sign', '/fd', 'sha256', '/sha1', $Thumbprint)
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'sha256') }
    & $signtool @arguments $file
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
}

Sign "$package\vcnsvcom.dll"
Remove-Item "$package\vcnsvcom.cat" -ErrorAction SilentlyContinue
& $inf2cat /driver:$package /os:10_RS5_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) { throw 'Catalog generation failed.' }
Sign "$package\vcnsvcom.cat"

# Informational: a test certificate shows as untrusted until it is added to the machine's trusted stores.
Get-AuthenticodeSignature "$package\vcnsvcom.dll", "$package\vcnsvcom.cat" |
    ForEach-Object { '{0}: {1} ({2})' -f (Split-Path $_.Path -Leaf), $_.SignerCertificate.Subject, $_.Status }
