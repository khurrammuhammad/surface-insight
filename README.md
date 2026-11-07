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

Prefer one command that picks the right build, verifies it, adds a Start menu shortcut and starts the monitor? In PowerShell:

```powershell
irm https://raw.githubusercontent.com/khurrammuhammad/surface-insight/main/scripts/install.ps1 | iex
```

Run the same command again to update. Your history is kept. To remove it:

```powershell
irm https://raw.githubusercontent.com/khurrammuhammad/surface-insight/main/scripts/install.ps1 -OutFile install.ps1
.\install.ps1 -Uninstall          # add -RemoveData to delete your history too
```

No administrator rights are needed to install. You can read the [installer](scripts/install.ps1) before running it.

### "Windows protected your PC"

The builds are not code-signed yet, so Windows SmartScreen may warn the first time. Choose **More info > Run anyway**. If you downloaded the zip in a browser, right-click it, open **Properties** and tick **Unblock** before extracting (the installer does this for you). You can check a download against `SHA256SUMS.txt`:

```powershell
(Get-FileHash .\SurfaceInsight-win-x64.zip -Algorithm SHA256).Hash
```

### Check that it works on a new PC

```powershell
.\SurfaceInsight.exe --self-test
Get-Content .\self-test.txt
```

Every line should say `PASS`, and the `INFO` line should say `(native)`. The live checks need a battery; on a desktop PC add `--ci`. Please open an issue for any `FAIL` line.

## FAQ

**Does it send my data anywhere?** No. History is stored in `%LOCALAPPDATA%\SurfaceInsight` and nothing is uploaded; there is no account, analytics or update check. The one thing that touches the network is *reverse DNS* (looking up the owner name of an address you saw in the Network page). It sends a DNS query for that address to your DNS server, runs only when you click it or turn on the optional Settings switch, and is off by default.

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

To publish a release, bump `<Version>` in `src/SurfaceInsight/SurfaceInsight.csproj`, then push a tag. GitHub Actions builds both architectures, self-tests them, and attaches the files and checksums to the release.

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

History includes per-process CPU, GPU, NPU and memory use, battery readings, and (if you turn network monitoring on) which applications connected to which addresses and names. It is stored unencrypted in `%LOCALAPPDATA%\SurfaceInsight`. Don't share that folder or attach it to bug reports.

## Contributing

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). Third-party data and packages are listed in [NOTICE.md](NOTICE.md).
