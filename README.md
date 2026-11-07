<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="brand/surface-insight-lockup-tagline-dark.png">
  <img src="brand/surface-insight-lockup-tagline-light.png" alt="Surface Insight: battery, CPU and network insight for Windows" width="560">
</picture>

<br><br>

[![Latest release](https://img.shields.io/github/v/release/khurrammuhammad/surface-insight?style=flat-square&color=2f6bff)](https://github.com/khurrammuhammad/surface-insight/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/khurrammuhammad/surface-insight/total?style=flat-square&color=2f6bff)](https://github.com/khurrammuhammad/surface-insight/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/khurrammuhammad/surface-insight/build.yml?style=flat-square)](https://github.com/khurrammuhammad/surface-insight/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-2f6bff?style=flat-square)](LICENSE)
![Windows 11 x64 and Arm64](https://img.shields.io/badge/Windows%2011-x64%20%7C%20Arm64-2f6bff?style=flat-square)

**Find out where your laptop's battery and performance actually go.**

[Download](#download) · [Features](#what-it-does) · [Install](#install) · [User guide](docs/Read-Me.md) · [Build from source](#build-from-source)

</div>

Surface Insight is a Windows tray monitor and dashboard. It records battery draw, CPU, GPU and NPU use per application, screen brightness and power-mode changes, and (optionally) which app talks to which host on the network. Then it helps you see *why* the battery drained or the fans spun up, instead of guessing.

It was built on a Surface, so it handles Surface hardware well, but it works on any Windows 11 laptop. **Everything stays on your PC.** No account, no cloud, no telemetry, and it never changes your system settings on its own.

<!--
Screenshots: add PNGs to docs/images/ and uncomment.
<p align="center"><img src="docs/images/now.png" alt="Now page: live draw, CPU, GPU and NPU tiles" width="880"></p>
<p align="center"><img src="docs/images/apps.png" alt="Apps page with architecture column" width="430"> <img src="docs/images/network.png" alt="Network page" width="430"></p>
-->

## What it does

- **Now.** Live device draw (watts), charge and runtime, CPU, GPU, NPU and network. Every tile is clickable and drills down to the detail behind it. A "Why did draw change?" card compares the last few minutes with the quieter part of the period.
- **Timeline.** Battery draw with screen brightness, power mode and charger changes marked. Drag across any chart to zoom, and stack the CPU use of chosen apps on top.
- **Apps.** Every application ranked by CPU, memory, I/O, GPU and NPU, grouped by executable, with an **Architecture** column (x64, x86, Arm64). On Arm devices, emulated x64/x86 apps are flagged, because they cost battery. Open an app to see its CPU against device power, its instances and its peak moments.
- **Network** (optional, off by default). Which apps connect to which addresses and names, traffic per app and destination in enhanced mode, a map, and *temporary* per-app blocking that Windows removes the moment you stop it.
- **Battery.** Reported health, discharge sessions and collection gaps. **Investigate** captures a 60-second trace and Windows battery and SleepStudy reports and summarizes them.
- Native builds for **x64** and **Arm64**, light and dark themes, CSV and HTML export.

## Download

| Your PC | Download |
|---|---|
| **Intel or AMD** (most laptops and desktops) | [SurfaceInsight-win-x64.zip](https://github.com/khurrammuhammad/surface-insight/releases/latest/download/SurfaceInsight-win-x64.zip) |
| **Arm** (Surface Pro 11th edition, Surface Laptop 7, Snapdragon X, Surface Pro X) | [SurfaceInsight-win-arm64.zip](https://github.com/khurrammuhammad/surface-insight/releases/latest/download/SurfaceInsight-win-arm64.zip) |

Not sure which one you have? Open **Settings > System > About** and read *System type*. The x64 build also runs on Arm through Windows' emulation, but it is slower and uses more battery, so pick Arm64 there.

Unzip into a permanent folder and run `SurfaceInsight.exe`. Single-file `.exe` versions (experimental) and `SHA256SUMS.txt` are on the [Releases page](https://github.com/khurrammuhammad/surface-insight/releases/latest).

### Install

For the per-release installer that selects the native build, verifies it and adds a Start menu shortcut, download and inspect the signed script in PowerShell:

```powershell
Invoke-WebRequest https://github.com/khurrammuhammad/surface-insight/releases/download/v1.1.1/install.ps1 -OutFile install.ps1
Get-AuthenticodeSignature .\install.ps1   # expect Valid and the release publisher
.\install.ps1
```

Your history is kept when you update. To remove it:

```powershell
Invoke-WebRequest https://github.com/khurrammuhammad/surface-insight/releases/download/v1.1.1/install.ps1 -OutFile install.ps1
.\install.ps1 -Uninstall          # add -RemoveData to delete your history too
```

No administrator rights are needed for the normal per-user install. Read the [installer](scripts/install.ps1) before running it. Every installation requires both a matching SHA-256 checksum and a verified file inventory. Unknown files are preserved, and a failed startup restores the previous inventoried files.

**Migrating from 1.1.0 or earlier:** old ZIP installs have no file ownership inventory, so automatic replacement is refused. Quit the old copy from the tray and install 1.1.1 into a **new empty dedicated folder**, for example `.\install.ps1 -InstallDir "$env:LOCALAPPDATA\Programs\SurfaceInsight-1.1.1"`. Existing history and settings in `%LOCALAPPDATA%\SurfaceInsight` remain available. Do not put data in the application folder, delete an existing `.old` folder, or run the old copy alongside the new one. Legacy ZIP uninstall must be done manually after checking that the folder is dedicated to the app. Single-file updates replace only the executable.

### Code signing and Windows warnings

The public **1.1.0** assets were unsigned. The **1.1.1 release workflow refuses unsigned publication**: it requires an approved code-signing certificate, timestamps and verifies the app executables and installer, retests signed executables on each native architecture, and produces provenance attestations. Development CI artifacts remain unsigned and are not public release downloads. Configuring signing and completing the Windows acceptance checklist are maintainer prerequisites; this source change does not mean a signed 1.1.1 release already exists.

Check `Get-AuthenticodeSignature .\SurfaceInsight.exe` and compare its publisher with the release notes. Signing does not guarantee SmartScreen reputation or eliminate antivirus warnings. Investigate unexpected publisher/signature/hash results; do not disable antivirus to install the app. Checksums detect changed downloads, but a checksum served from the same compromised origin is not independent publisher authentication.

### Updating

Open **Settings > Updates > Check for updates**. If a newer release exists you'll see what's new and an **Update now** button: the app downloads the right build for your PC, checks it against the release's `SHA256SUMS.txt`, restarts itself on the new version and keeps your history and settings. For inventoried installations, backups are retained until the replacement dashboard and collector acknowledge startup. Copy/startup failures trigger file-level recovery; if a subsequently modified file prevents safe rollback, the error identifies retained recovery backups rather than overwriting it. See [release and recovery guidance](docs/Release-Checklist.md). Tick **Check for updates automatically** if you want the tray monitor to look once a day and show a notification (it never installs without your click).

The first release with this button is 1.1.0. If you have 1.0.0, run the install command above once to get there. If Surface Insight is in a folder that needs administrator rights (for example `C:\Program Files`), **Update now** opens the release page instead. You can always re-run the install command.

Until the builds are code-signed, the checksum protects against a corrupt or altered download, but it can't protect against a compromised GitHub account.

### Check that it works on a new PC

```powershell
.\SurfaceInsight.exe --self-test
Get-Content .\self-test.txt
```

Every line should say `PASS`, and the `INFO` line should say `(native)`. The live checks need a battery; on a desktop PC add `--ci`. Please open an issue for any `FAIL` line.

## FAQ

**Does it send my data anywhere?** No. History is stored in `%LOCALAPPDATA%\SurfaceInsight` and nothing is uploaded; there is no account or analytics. Two optional things touch the network, and both are off until you use them: (1) **Check for updates** asks GitHub (`api.github.com`) for the latest release when you press the button, or once a day if you turn on the automatic check; GitHub sees your IP address and the request, and the app sends nothing else. (2) *Reverse DNS* (looking up the owner name of an address you saw in the Network page) sends a DNS query for that address to your DNS server when you click it or turn on its Settings switch.

**Why does it ask for administrator approval?** Only for the optional extras: *enhanced* network monitoring, temporary blocking, and *Investigate* captures. Each one asks every time, and nothing runs elevated permanently. No service or driver is installed.

**Which features need special hardware?** Battery watts need a battery, NPU figures need a Windows-visible NPU, and per-app GPU needs the Windows GPU counters. Fan speed and CPU temperature are not available on ordinary Windows sessions, so you can mark "Fans are loud now" to line up what you hear with recorded activity.

**Is it accurate?** Watts are whole-device, not per app, and the app says so wherever that matters. See [Measurement limits](docs/Read-Me.md#measurement-limits).

## Build from source

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\scripts\build.ps1 -Arch all                 # x64 and Arm64 zips in .\dist
.\scripts\build.ps1 -Arch arm64
.\scripts\build.ps1 -Arch all -SingleFile     # also single-file exes (experimental)
```

The script downloads the IP-location database (about 127 MB, see [NOTICE.md](NOTICE.md)) because it is too large for git, publishes self-contained builds, checks that each executable and its native libraries are really for that architecture, and zips them. The app builds and runs without the database; addresses then have no city location.

To prepare a release, follow [Release checklist](docs/Release-Checklist.md), including signing setup and acceptance on the exact commit. GitHub Actions builds and tests both forms on native x64/Arm64 runners, signs and retests them, generates checksums and provenance, and creates a **draft** release. The maintainer reviews that draft before public publication.

```powershell
git tag v1.1.1
git push origin v1.1.1
```

| Path | What |
|---|---|
| `src/SurfaceInsight` | The app (C#, .NET 10, WPF and a tray host) |
| `scripts/` | `build.ps1`, `stage-release.ps1`, `fetch-geo.ps1`, `install.ps1` |
| `docs/Read-Me.md` | User guide: pages, network monitoring, limits |
| `brand/` | Icon and wordmark images |

## Privacy

History includes per-process CPU, GPU, NPU and memory use, battery readings, and (if you turn network monitoring on) which applications connected to which addresses and names. It is stored unencrypted in `%LOCALAPPDATA%\SurfaceInsight`. Diagnostic captures are retained separately from routine history and may include system paths and process metadata. Clearing routine history does not clear captures and is not secure forensic erasure. Don't share that folder or attach it to public bug reports.

## Contributing

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). Third-party data and packages are listed in [NOTICE.md](NOTICE.md).
