## Which download do I need?

| Your PC | Download |
|---|---|
| **Intel or AMD** (most laptops and desktops) | [`SurfaceInsight-win-x64.zip`](https://github.com/khurrammuhammad/surface-insight/releases/download/{{TAG}}/SurfaceInsight-win-x64.zip) |
| **Arm** (Surface Pro 11th edition, Surface Laptop 7, Snapdragon X, Surface Pro X) | [`SurfaceInsight-win-arm64.zip`](https://github.com/khurrammuhammad/surface-insight/releases/download/{{TAG}}/SurfaceInsight-win-arm64.zip) |

Not sure? Open **Settings > System > About** and look at *System type*. The x64 build also runs on Arm, but through emulation, so it is slower and uses more battery.

Unzip to a permanent folder and run `SurfaceInsight.exe`. Or install and update with one command in PowerShell:

```powershell
irm https://raw.githubusercontent.com/khurrammuhammad/surface-insight/main/scripts/install.ps1 | iex
```

A single-file `SurfaceInsight-win-<arch>.exe` is attached when it passed the automated self-test (experimental; it unpacks itself to a cache folder on first start). Verify any download against `SHA256SUMS.txt`.

These builds are not code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Choose **More info > Run anyway**.

---

