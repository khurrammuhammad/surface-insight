## Which download do I need?

| Your PC | Download |
|---|---|
| **Intel or AMD** (most laptops and desktops) | [`SurfaceInsight-win-x64.zip`](https://github.com/khurrammuhammad/surface-insight/releases/download/{{TAG}}/SurfaceInsight-win-x64.zip) |
| **Arm** (Surface Pro 11th edition, Surface Laptop 7, Snapdragon X, Surface Pro X) | [`SurfaceInsight-win-arm64.zip`](https://github.com/khurrammuhammad/surface-insight/releases/download/{{TAG}}/SurfaceInsight-win-arm64.zip) |

Not sure? Open **Settings > System > About** and look at *System type*. The x64 build also runs on Arm, but through emulation, so it is slower and uses more battery.

Unzip into a new dedicated folder and run `SurfaceInsight.exe`. For the signed installer, download `install.ps1` from this release, inspect it, and check `Get-AuthenticodeSignature .\install.ps1` before running `.\install.ps1`. Do not execute a mutable `main` script through `iex`.

**Migration from 1.1.0 ZIP installations:** quit the old tray monitor and use a new empty folder. Old ZIP folders have no file inventory and are deliberately not overwritten automatically. History/settings in LocalAppData are kept. Never delete a pre-existing `.old` folder to make an update work.

Both ZIP and experimental single-file variants are tested natively on the advertised architecture, then signed, timestamped, verified and retested. Verify SHA-256 against `SHA256SUMS.txt`; provenance is available through GitHub artifact attestations. A valid signature does not guarantee that SmartScreen or enterprise policy will allow the app.

This is a **draft until the maintainer verifies the Windows acceptance record, publisher identity, downloads and recovery behavior**. The signing certificate subject and exact commit must be recorded here before publication.

---

