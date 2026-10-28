<#
.SYNOPSIS
    Checks a built driver package against the rules Microsoft applies before it will sign one: InfVerif in WHQL mode,
    and ApiValidator for the APIs a Windows Driver may call. CI runs it on every build, so the package stays ready for
    attestation signing and certification.
#>
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$kit = & "$PSScriptRoot\Kit.ps1"
$package = "$root\out\$Configuration\package"

& $kit.InfVerif /h "$package\vcnsvcom.inf"
if ($LASTEXITCODE -ne 0) { throw 'The INF does not meet the WHQL signature requirements.' }

& $kit.ApiValidator "-DriverPackagePath:$package" "-SupportedApiXmlFiles:$($kit.UniversalDdis)\UniversalDDIs.xml" "-ModuleWhiteListXmlFiles:$($kit.UniversalDdis)\ModuleWhitelist.xml"
if ($LASTEXITCODE -ne 0) { throw 'The driver calls APIs that a Windows Driver may not use.' }
