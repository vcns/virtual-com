# Security

## Reporting a vulnerability

Please don't open a public issue. Report it privately on GitHub instead: on the repository's **Security** tab, choose **Report a vulnerability**. Include the version (the bridge's About tab, or the file version of `vcomctl.exe` or `vcnsvcom.dll`) and the steps to reproduce.

We'll acknowledge the report, keep you informed, and credit you in the release notes unless you'd rather not be named.

## Supported versions

Fixes go into a new release; the latest release is the supported one. Builds from `main` and from pull requests are test builds.

The programs are self-contained .NET executables, so they carry the .NET runtime they were built with. A release made after a .NET security update includes it.

## Scope

- The driver (`vcnsvcom.dll` and its INF), the library, `vcomctl` and the bridge, including the bridge's service and its elevated helper.
- The release and CI workflows, and the signing of what they produce.

By design, a shared port carries the device's own protocol over TCP, unencrypted and usually without authentication; access is limited to its allowed clients and a matching firewall rule. A report that something gets past those limits is in scope. That the traffic isn't encrypted isn't a vulnerability; see [Security](https://vcns.github.io/virtual-com/bridge.html#security) on the bridge page.
