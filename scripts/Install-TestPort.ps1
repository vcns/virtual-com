<#
.SYNOPSIS
    Development only: trusts the test-signing certificate, installs the driver package and creates a port.
    Run elevated. Undo with Uninstall-Test.ps1.
#>
param(
    [string] $Port = 'COM20',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = "$root\out\test-signing"
$log = "$out\install-log.txt"
Start-Transcript -Path $log -Force | Out-Null
try {
    $vcomctl = (Get-ChildItem "$root\src\vcomctl\bin\*\net10.0-windows\vcomctl.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $vcomctl) { throw 'Build src\vcomctl first (dotnet build VirtualCom.slnx).' }
    $package = "$root\out\$Configuration\package"

    Write-Host "== Environment"
    Write-Host "Windows: $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion) build $([Environment]::OSVersion.Version.Build)"
    Write-Host "Secure Boot enabled: $(try { Confirm-SecureBootUEFI } catch { "unknown ($($_.Exception.Message))" })"
    Write-Host "Test signing: $(((bcdedit /enum '{current}') | Select-String testsigning) -join ' ')"

    Write-Host "== Trusting the test-signing certificate"
    foreach ($store in 'Root', 'TrustedPublisher') {
        Import-Certificate -FilePath "$out\vcom-test-signing.cer" -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        Write-Host "Added to LocalMachine\$store"
    }

    Write-Host "== Installing the driver package"
    & $vcomctl install-driver "$package\vcnsvcom.inf"

    Write-Host "== Creating $Port"
    & $vcomctl create $Port
    Write-Host "exit code $LASTEXITCODE"

    Write-Host "== Result"
    & $vcomctl list
    Write-Host "SERIALCOMM:"
    (Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM').PSObject.Properties | Where-Object Name -notlike 'PS*' |
        ForEach-Object { Write-Host "  $($_.Name) = $($_.Value)" }
    Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue | Where-Object InstanceId -like 'ROOT\VCNSVCOM*' |
        Format-Table Status, FriendlyName, InstanceId, Problem -AutoSize | Out-String | Write-Host

    Write-Host "== setupapi.dev.log (last install section)"
    $setupLog = Get-Content "$env:windir\INF\setupapi.dev.log" -Tail 400
    $start = ($setupLog | Select-String -Pattern 'VCNSVCOM|vcnsvcom' | Select-Object -First 1).LineNumber
    if ($start) { $setupLog[($start - 5)..($setupLog.Count - 1)] | Select-String -Pattern '!!!|!\s|Error|error|Failed|signature|Signer|Driver Store|Install Device|Exit status' | Select-Object -Last 40 | ForEach-Object { Write-Host $_.Line } }
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
}
finally {
    Stop-Transcript | Out-Null
}
