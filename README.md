# virtual-com

A user-mode (UMDF 2) virtual serial port driver for Windows 10 and 11. Each port has two ends:

- **COM end**: an ordinary `COMn` port. Any serial application opens it as usual.
- **Host end**: a private device interface that your own application opens to supply the data. Typical uses are bridging to TCP, logging, or emulating a device.

Bytes written to one end are read from the other, unchanged. The COM end behaves like a real serial port:
- Win32 read and write time-outs, `WaitCommEvent` events, purge, and queue status.
- Port properties, modem lines and break.
- It works with .NET `SerialPort`, including the `DataReceived` and `PinChanged` events.

```
 Serial application ──► COM20 ══ vcnsvcom driver ══ host interface ──► your application (e.g. TCP bridge)
```

> **Status: early development.** It passes its test suite on Windows 11 25H2 with Secure Boot on. It has not yet been released with a production signature: see [Signing](#signing).

## Why user mode

The driver is a UMDF DLL loaded by Windows' own driver host. It contains no kernel-mode code, so:
- Windows' kernel driver-signing rules don't apply. Since Windows 10 1607 (and more strictly since the 2026 removal of cross-signed driver trust), those rules require Microsoft's signature. The driver package needs only a normal code-signing certificate.
- A bug in the driver can't crash the machine.

## How it works

| Piece | Purpose |
| --- | --- |
| `driver/` | The UMDF 2 driver (`vcnsvcom.dll` + `vcnsvcom.inf`). One device per port, Ports class, hardware ID `Root\VcnsVirtualCom`. |
| `include/vcom.h` | Public interface: host-end interface GUID and control codes. |
| `src/Vcns.VirtualCom` | .NET library: create, list and remove ports (`VirtualComDevices`) and open the host end (`VirtualComHost`). |
| `src/vcomctl` | Command-line tool built on the library. |
| `tests/` | Behaviour tests against a real installed port, using `System.IO.Ports`. |

The COM end's modem lines (CTS, DSR, DCD) are raised while the host end is open, so applications see a "cable" only when something is attached. The host can change them with `VirtualComHost.SetModemLines`. With nothing attached, writes from the application are discarded, as on an unplugged line.

### Using the library

```csharp
// Administrator, once: put the signed driver package in the driver store, then create a port.
VirtualComDevices.InstallDriver(@"package\vcnsvcom.inf");
VirtualComDevices.Create("COM20");

// Any user: be the host end.
using var host = VirtualComHost.Open("COM20");
host.SetModemLines(ModemLines.None);          // e.g. until your upstream connection is ready
await host.Stream.WriteAsync(data);            // read by the application on COM20
int n = await host.Stream.ReadAsync(buffer);   // returns as soon as the application has written something
var state = host.GetState();                   // is COM20 open? which baud rate, DTR/RTS did it set?
```

### Command line

```
vcomctl list
vcomctl install-driver <path-to-vcnsvcom.inf>   (administrator)
vcomctl create COM20                            (administrator)
vcomctl remove COM20                            (administrator)
```

`create` sets the COM number you choose and claims it in Windows' COM name database.

## Building

You need:
- Visual Studio 2022 Build Tools, with the **Desktop development with C++** workload and **MSVC v143 Spectre-mitigated libraries**;
- the .NET 10 SDK;
- `nuget.exe`.

The Windows SDK and WDK come from NuGet. No WDK installation or Visual Studio WDK extension is needed: the driver builds with the standard v143 toolset (see `build/Umdf.props`).

```powershell
nuget restore packages.config -PackagesDirectory packages
msbuild driver\vcnsvcom.vcxproj -p:Configuration=Release -p:Platform=x64   # -> out\Release\package\
dotnet build VirtualCom.slnx
```

The package in `out\Release\package` contains `vcnsvcom.dll`, `vcnsvcom.inf` and an unsigned `vcnsvcom.cat`.

## Signing

Windows installs the package only if its catalog is signed by a certificate it trusts. Sign with:

```powershell
build\Sign-Package.ps1 -Thumbprint <code-signing certificate thumbprint>
```

This signs the DLL, regenerates the catalog to cover the signed DLL, and signs the catalog.

Release builds will be signed by **VCNS Tech Ltd**. Until then, development uses a self-signed certificate.

### Development install (test certificate)

1. Create a test certificate:
   ```powershell
   New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=VCNS Virtual COM (test signing)' -CertStoreLocation Cert:\CurrentUser\My
   ```
2. Export it to `out\test-signing\vcom-test-signing.cer`, and sign the package with it.
3. In an elevated PowerShell, run `scripts\Install-TestPort.ps1 -Port COM20`. It trusts the certificate machine-wide (Root and Trusted Publishers), installs the package and creates the port. A log is written to `out\test-signing\install-log.txt`.

`scripts\Uninstall-Test.ps1` removes the ports, the driver package and the certificate. Trusting a self-signed certificate lets anything signed with it install on that machine, so keep the private key to yourself and remove the trust when you're done.

## Tests

```powershell
$env:VCOM_TEST_PORT = 'COM20'
dotnet test tests\Vcns.VirtualCom.Tests
```

Without `VCOM_TEST_PORT`, the port tests are skipped. The CI workflow builds and test-signs the driver, installs it on the GitHub-hosted Windows runner, and runs the full suite.

## Limitations

- x64 only for now (ARM64 needs WHQL-signed driver packages).
- Serial settings (baud rate, parity, flow control) are stored and reported to the host but don't change the data. There is no physical line.
- Each end can be open by one handle at a time, as with a real serial port.

## Licence

[Microsoft Public License (MS-PL)](LICENSE). Parts of the driver and its INF are adapted from Microsoft's [VirtualSerial2 sample](https://github.com/microsoft/Windows-driver-samples/tree/main/serial/VirtualSerial2), which is also MS-PL. Those files keep Microsoft's copyright notice.
