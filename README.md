# Surface Insight

A Windows tray monitor and dashboard that explains where your laptop's battery and performance go: battery draw, CPU, GPU and NPU per application, screen brightness and power-mode effects, and optional network monitoring (which app talks to which host, with temporary per-app blocking).

- Runs on **Windows 11 x64** and **Windows 11 Arm64** (native builds for each).
- Everything stays on the laptop. No cloud requests, no automatic system changes. History is in `%LOCALAPPDATA%\SurfaceInsight` and is never part of this repo.
- Full user guide: [docs/Read-Me.md](docs/Read-Me.md).

## Install on a laptop

One-time setup, in PowerShell:

```powershell
winget install GitHub.cli
gh auth login
```

Then download and run the installer from this repo (the repo is private, so use `gh`):

```powershell
gh api repos/OWNER/surface-insight/contents/scripts/install.ps1 -H "Accept: application/vnd.github.raw" > install.ps1
.\install.ps1
```

It detects whether the laptop is x64 or Arm64, downloads the matching zip from the latest release, installs to `%LOCALAPPDATA%\Programs\SurfaceInsight`, adds a Start menu shortcut and starts the monitor. **Run the same script again to update.** Your history is kept.

Prefer to do it by hand? Open the repo's **Releases** page, download `SurfaceInsight-<version>-win-x64.zip` or `...-win-arm64.zip` (pick the one that matches the laptop), unzip it somewhere permanent, right-click `SurfaceInsight.exe` and run it. (If Windows marks the files as downloaded from the internet, unblock the zip before unzipping: right-click, Properties, Unblock.)

An x64 build also runs on an Arm64 laptop through Windows' emulation, but it is slower and uses more battery, so use the Arm64 zip there. Settings → *About this build* shows which one is running.

### Check that it works on a new laptop

```powershell
.\SurfaceInsight.exe --self-test
Get-Content .\self-test.txt
```

Every line should say `PASS`, and the `INFO` line should say `(native)`. The live checks need a battery; on a desktop PC run `--self-test --ci`. Please report any `FAIL` lines.

Some features depend on the hardware: battery watts need a battery, NPU figures need a Windows-visible NPU, and network *enhanced* monitoring and temporary blocking need administrator approval each time you start them.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows.

```powershell
.\scripts\build.ps1 -Arch all      # x64 and arm64 zips in .\dist
.\scripts\build.ps1 -Arch arm64
```

The script downloads the IP-location database (about 127 MB; see [NOTICE.md](NOTICE.md)) because it is too large for git, publishes self-contained builds, checks each executable and its native libraries really are for that architecture, and zips them. The app builds and runs without the database; addresses then have no city location.

A release is created by pushing a version tag, which runs the GitHub Actions workflow:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

Bump `<Version>` in `src/SurfaceInsight/SurfaceInsight.csproj` first. The workflow builds both zips, runs the self-tests on the x64 build, and attaches both zips to the release.

## Layout

| Path | What |
|---|---|
| `src/SurfaceInsight` | The app (C#, .NET 10, WPF + tray host) |
| `scripts/` | `build.ps1`, `fetch-geo.ps1`, `install.ps1` |
| `docs/Read-Me.md` | User guide: pages, network monitoring, limits |
| `brand/` | Icon and wordmark images |

## Privacy

History includes per-process CPU, GPU, NPU and memory use, battery readings, and (if you turn network monitoring on) which applications connected to which addresses and names. It is stored unencrypted in `%LOCALAPPDATA%\SurfaceInsight` and is excluded from git by design. Do not commit or share that folder.
