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

The repository also contains a ready-made host application, the **VCNS Virtual COM Bridge**, which connects virtual COM ports to TCP devices (see [Bridge applications](#bridge-applications)).

> **Status: version 1.0.** It passes its test suite on Windows 11 25H2 with Secure Boot on, and every change is tested in CI. Releases are signed with the project's test certificate until VCNS Tech Ltd's publicly trusted one is in place: see [Signing](#signing).

**Documentation:** <https://vcns.github.io/virtual-com/>: use cases, user and developer guides, reference, building and FAQ. The site's source is in [gh-pages/](gh-pages/).

## Why user mode

The driver is a UMDF DLL loaded by Windows' own driver host. It contains no kernel-mode code, so:
- Windows' kernel driver-signing rules don't apply. Since Windows 10 1607 (and more strictly since the 2026 removal of cross-signed driver trust), those rules require Microsoft's signature. The driver package needs only a normal code-signing certificate.
- A bug in the driver makes Windows restart its driver host process, rather than stopping the machine.

## How it works

| Piece | Purpose |
| --- | --- |
| `driver/` | The UMDF 2 driver (`vcnsvcom.dll` + `vcnsvcom.inf`). One device per port, Ports class, hardware ID `Root\VcnsVirtualCom`. |
| `include/vcom.h` | Public interface: host-end interface GUID and control codes. |
| `src/Vcns.VirtualCom` | .NET library: create, list and remove ports (`VirtualComDevices`) and open the host end (`VirtualComHost`). |
| `src/vcomctl` | Command-line tool built on the library. |
| `src/Vcns.VirtualCom.Bridge` | Bridge engine: profiles, configuration, COM ↔ TCP client sessions, server mode (shared COM ports and its Windows service), single-session locking, port allocation, and an elevated helper that creates ports with the embedded driver package and applies server settings. |
| `src/Vcns.VirtualCom.Desktop` | The bridge's tray application and Settings window, shared by every edition. |
| `src/vcombridge` | The generic edition: **VCNS Virtual COM Bridge**. |
| `tests/` | Driver behaviour tests against a real installed port, using `System.IO.Ports`, and the bridge's unit, loopback and end-to-end tests. |

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
vcomctl label COM20 "Workshop test rig"         (administrator; no text restores the standard name)
```

Exit codes: 0 done, 1 error, 2 port created but not running, and 3010 (the standard "restart required" code) when `install-driver` succeeded but existing ports need a restart to use the new driver.

`create` sets the COM number you choose and claims it in Windows' COM name database. `label` sets the name Device Manager shows, e.g. "Workshop test rig (COM20)" (`VirtualComDevices.SetDisplayName` in the library).

## Bridge applications

The bridge works in two directions.

**Client mode** gives a serial application a COM port that leads to a device's TCP port:

```
 Serial application ──► COM9 ══ vcnsvcom ══ VCNS Virtual COM Bridge ──TCP──► device (e.g. 192.168.10.50:5200)
```

- **Profiles** map one virtual COM port to one TCP endpoint. New ports are numbered above every COM port already used or reserved on the PC.
- **Labelled for many customers:** each profile can carry a customer, label, device (make and model) and notes. The tray menu groups profiles by customer, Settings can filter and sort them, and each virtual port is named after its customer and label in Device Manager.
- **On demand:** nothing connects until you start a profile from the tray menu, and nothing reconnects by itself.
- **Single-session safe:** at most one connection per endpoint, a release interval between connections, and a device that drops a second client is reported as **in use** rather than retried.
- **Transparent:** bytes are forwarded unchanged in both directions, as soon as they arrive.
- The COM end's modem lines rise only once the device has accepted the session.
- Creating or removing ports asks for administrator approval once per change; everything else runs as a normal user.

**Server mode** shares this PC's real COM ports, such as USB-attached devices, on TCP ports:

```
 Client (bridge in client mode, monitoring, any TCP client) ──TCP 4003──► bridge service ──► COM3 ──► device
```

- Each shared port behaves like a single-session serial device server: raw TCP with no handshake, one client at a time, and any other client accepted and closed straight away (client mode reports it as **in use**).
- The COM port is opened only while a client is connected, with fixed per-port baud rate, format and flow control.
- TCP ports are suggested as the edition's base plus the COM number (4000 + n generically, 5200 + n for NovaStar Proxy), skipping ports in use or reserved.
- A Windows service (`--service`, running as LocalService) does the work, so ports stay available with nobody logged in. Its settings are in `%ProgramData%`, changeable only by administrators.
- Each port listens on one chosen address, serves only its allowed clients, and gets a firewall rule limited to them. It's off until you turn it on in **Settings → Server ports**.

### Editions

An edition is a small executable that runs the shared application with its own name, data folder and wording:

```csharp
[STAThread]
static int Main(string[] args) => BridgeApp.Run(new BridgeEdition
{
    ProductName = "Example Bridge",
    DataFolderName = "ExampleBridge",     // %APPDATA% and %LOCALAPPDATA% folder; also the single-instance lock
    LogFilePrefix = "example-bridge",
    SerialApplication = "the console",    // used in messages: "the console opens COM9"
    DeviceNoun = "controller",
    DefaultTargetPort = 4001,
    OtherClientExample = "the monitoring system",
    ServerPortBase = 4000,                // server mode: COM3 is shared on TCP 4003
}, args);
```

NovaStar Proxy is the edition for NovaStar LED controllers: NovaLCT wording, port 5200 by default (and 5200 + COM number in server mode), and a reminder to pause Zabbix polling before connecting.

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

The version is `VersionPrefix` in `Directory.Build.props`, plus a fourth part: CI's run number, or 0 for a local build. The driver package, the DLL and the programs all carry it.

Once the package is signed (below), the bridge embeds it. To publish the bridge as a single self-contained executable in `out\publish\vcombridge`:

```powershell
dotnet publish src\vcombridge -p:PublishProfile=win-x64
```

`build\Publish-Programs.ps1 -Thumbprint <thumbprint>` publishes the bridge and `vcomctl` this way and signs them.

## Signing

Windows installs the package only if its catalog is signed by a certificate it trusts. Sign with:

```powershell
build\Sign-Package.ps1 -Thumbprint <code-signing certificate thumbprint>
```

This signs the DLL, regenerates the catalog to cover the signed DLL, and signs the catalog.

Releases are signed in CI with the certificate in the Production environment: for now a copy of the CI test certificate, later a publicly trusted **VCNS Tech Ltd** certificate. With that, nothing needs importing, but Windows still asks an administrator to confirm the publisher the first time; only Microsoft's signature avoids that prompt. Microsoft signing, which ARM64, S mode and Windows Update need, is planned in stages: see [Microsoft signing](https://vcns.github.io/virtual-com/microsoft-signing.html). `build\Test-Package.ps1` checks the package against Microsoft's pre-signing rules, and CI runs it on every build.

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
dotnet test tests\Vcns.VirtualCom.Bridge.Tests
```

Without `VCOM_TEST_PORT`, the port tests and the bridge's end-to-end tests are skipped. On every pull request and push to `main`, the CI workflow (`.github/workflows/build.yml`) builds and test-signs the driver, installs it on a GitHub-hosted Windows runner and runs both suites. Pushes to `main` also upload test builds of the driver package and the bridge, each with the certificate to trust, and the driver's symbols. The certificate those builds are signed with, and how to set it up (`scripts\Set-GitHubEnvironments.ps1`), are described under [Continuous integration](https://vcns.github.io/virtual-com/building.html#ci).

## Releases

Releases are on the [releases page](https://github.com/vcns/virtual-com/releases). To make one, set `VersionPrefix` in `Directory.Build.props`, merge, then tag the merge commit `v<version>` and push the tag. Once a maintainer approves the job, CI builds, tests and signs the release and creates a draft GitHub release, which a maintainer then reads and publishes. See [Releases](https://vcns.github.io/virtual-com/building.html#releases). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Limitations

- x64 only for now (ARM64 needs WHQL-signed driver packages).
- Serial settings (baud rate, parity, flow control) are stored and reported to the host but don't change the data. There is no physical line.
- Each end can be open by one handle at a time, as with a real serial port.

## Licence

[Microsoft Public License (MS-PL)](LICENSE). Parts of the driver and its INF are adapted from Microsoft's [VirtualSerial2 sample](https://github.com/microsoft/Windows-driver-samples/tree/main/serial/VirtualSerial2), which is also MS-PL. Those files keep Microsoft's copyright notice.
