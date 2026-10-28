<#
.SYNOPSIS
    Development only: trusts the test-signing certificate, installs the driver package and creates a port.
    Run elevated. Run it again after rebuilding to upgrade the driver; an existing port is kept. Undo with
    Uninstall-Test.ps1. Exits with 0 when the port is running and 1 otherwise; CI uses it too.
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
$failed = $false
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
    if ($LASTEXITCODE -eq 3010) { throw 'The driver package is installed, but Windows needs a restart before existing ports use it. Restart, then run this again.' }
    if ($LASTEXITCODE -ne 0) { throw "Installing the driver package failed (exit code $LASTEXITCODE)." }

    $existing = & $vcomctl list | Where-Object { ($_ -split '\s+')[0] -eq $Port }
    if ($existing) {
        Write-Host "== $Port already exists"
        Write-Host $existing
        if ($existing -match 'not running') { throw "$Port exists but isn't running." }
    }
    else {
        Write-Host "== Creating $Port"
        & $vcomctl create $Port
        if ($LASTEXITCODE -ne 0) { throw "Creating $Port failed (exit code $LASTEXITCODE)." }
    }

    Write-Host "== Result"
    & $vcomctl list
    Write-Host "SERIALCOMM:"
    (Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM').PSObject.Properties | Where-Object Name -notlike 'PS*' |
        ForEach-Object { Write-Host "  $($_.Name) = $($_.Value)" }
    Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue | Where-Object InstanceId -like 'ROOT\VCNSVCOM*' |
        Format-Table Status, FriendlyName, InstanceId, Problem -AutoSize | Out-String | Write-Host
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    $failed = $true
}
finally {
    # Written whether or not the install worked: when it didn't, this is where the reason is. Reading the log mustn't
    # stop the transcript closing or change the exit code.
    Write-Host "== setupapi.dev.log (last install section)"
    try {
        $setupLog = @(Get-Content "$env:windir\INF\setupapi.dev.log" -Tail 400)
        $start = ($setupLog | Select-String -Pattern 'VCNSVCOM|vcnsvcom' | Select-Object -First 1).LineNumber
        if ($start) { $setupLog[[Math]::Max(0, $start - 5)..($setupLog.Count - 1)] | Select-String -Pattern '!!!|!\s|Error|error|Failed|signature|Signer|Driver Store|Install Device|Exit status' | Select-Object -Last 40 | ForEach-Object { Write-Host $_.Line } }
    }
    catch {
        Write-Host "Couldn't read setupapi.dev.log: $($_.Exception.Message)"
    }
    Stop-Transcript | Out-Null
}
if ($failed) { exit 1 }
exit 0
