<#
.SYNOPSIS
    Authenticode-signs files (programs, DLLs, catalogs) with SHA-256 and an RFC 3161 time stamp.

.PARAMETER Path
    The files to sign.

.PARAMETER Thumbprint
    SHA-1 thumbprint of a code-signing certificate in the current user's or local machine's "My" store.

.PARAMETER TimestampUrl
    RFC 3161 time-stamping service, so signatures stay valid after the certificate expires. Pass '' to skip.
#>
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [Parameter(Mandatory)] [string] $Thumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$signtool = (& "$PSScriptRoot\Kit.ps1").SignTool
$arguments = @('sign', '/fd', 'sha256', '/sha1', $Thumbprint)
if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'sha256') }

foreach ($file in $Path) {
    & $signtool @arguments $file
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
}
