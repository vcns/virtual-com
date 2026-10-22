<#
.SYNOPSIS
    Development only: removes every VCNS virtual COM port, the driver package and the test-signing certificate.
    Run elevated.
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$log = "$root\out\test-signing\uninstall-log.txt"
Start-Transcript -Path $log -Force | Out-Null
try {
    $vcomctl = (Get-ChildItem "$root\src\vcomctl\bin\*\net10.0-windows\vcomctl.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue | Where-Object InstanceId -like 'ROOT\VCNSVCOM*' | ForEach-Object {
        & $vcomctl remove $_.InstanceId
    }

    # Remove our package(s) from the driver store.
    $published = pnputil /enum-drivers | Out-String
    foreach ($block in $published -split "(\r?\n){2,}") {
        if ($block -match 'Original Name:\s+vcnsvcom\.inf' -and $block -match 'Published Name:\s+(oem\d+\.inf)') {
            pnputil /delete-driver $Matches[1] /uninstall /force
        }
    }

    Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |
        Where-Object Subject -eq 'CN=VCNS Virtual COM (test signing)' |
        ForEach-Object { Remove-Item $_.PSPath; Write-Host "Removed certificate from $($_.PSParentPath)" }
}
finally {
    Stop-Transcript | Out-Null
}
