<#
.SYNOPSIS
    Returns the paths of the Windows SDK and WDK tools restored from NuGet (see packages.config and
    Directory.Build.props), so the build scripts agree on where the kits are.
#>
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.WdkNuGetVersion
$kit = $props.Project.PropertyGroup.WdkSdkVersion
$sdk = "$root\packages\Microsoft.Windows.SDK.CPP.$version\c"
$wdk = "$root\packages\Microsoft.Windows.WDK.x64.$version\c"

[pscustomobject]@{
    SignTool      = "$sdk\bin\$kit\x64\signtool.exe"
    Inf2Cat       = "$wdk\bin\$kit\x86\Inf2Cat.exe"
    InfVerif      = "$wdk\tools\$kit\x64\infverif.exe"
    ApiValidator  = "$wdk\bin\$kit\x64\apivalidator.exe"
    UniversalDdis = "$wdk\build\$kit\universalDDIs\x64"
}
