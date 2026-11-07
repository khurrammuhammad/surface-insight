# Changelog

All notable changes. Versions follow [Semantic Versioning](https://semver.org).

## 1.1.0

### Added
- **Architecture column on the Apps page.** Shows whether each app is x64, x86 (32-bit), Arm64 or Arm (32-bit). On Arm64 Windows, x64 and x86 apps are marked *emulated*, there is an *Emulated* filter, and the app detail page shows the architecture of each instance.
- **Drill-down on the Now page.** The Device draw, Charge, CPU, GPU, NPU and Network tiles open the relevant page (Apps opens sorted by the tile's metric). The draw chart, Power card and "Why draw changed" card link to the Timeline or Battery.
- **Easier installs.** One-line PowerShell installer that needs no GitHub CLI, verifies the download against `SHA256SUMS.txt`, updates in place and can uninstall. Stable download links that always point to the latest release. Experimental single-file `.exe` builds for x64 and Arm64, published only when they pass the automated self-test.
- Self-test checks for architecture detection and for the bundled native libraries.
- MIT license, contributing guide, security policy and issue templates.

### Changed
- The user guide no longer refers to a private Surface test machine and documents the new features.

## 1.0.0
- First release: battery draw, CPU, GPU and NPU per application, timeline, battery health, optional network monitoring with temporary blocking, native x64 and Arm64 builds.
