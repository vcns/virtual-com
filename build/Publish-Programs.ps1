<#
.SYNOPSIS
    Publishes the bridge and vcomctl as single executables in out\publish, and signs them if a certificate is given.
    The bridge embeds the driver package in out\Release\package, so build and sign that first.

.PARAMETER Thumbprint
    SHA-1 thumbprint of the code-signing certificate, as for Sign-Package.ps1. Without it, the programs aren't signed.

.PARAMETER TimestampUrl
    RFC 3161 time-stamping service. Pass '' to skip.
#>
param(
    [string] $Thumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

foreach ($project in 'vcombridge', 'vcomctl') {
    dotnet publish "$root\src\$project" -p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

$programs = "$root\out\publish\vcombridge\vcombridge.exe", "$root\out\publish\vcomctl\vcomctl.exe"
if ($Thumbprint) {
    & "$PSScriptRoot\Sign-Files.ps1" $programs -Thumbprint $Thumbprint -TimestampUrl $TimestampUrl
}
