# Contributing

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Use **Issues > New issue > Bug report**. It asks for three things that make most bugs quick to fix:

1. The build line from **Settings > About this build** (version and x64/Arm64).
2. Your device model and Windows version.
3. The contents of `self-test.txt` after running `SurfaceInsight.exe --self-test`.

Please **don't attach your history folder** (`%LOCALAPPDATA%\SurfaceInsight`) or paste network history. It lists the apps you use and the hosts they talk to. Screenshots are fine, but check them for names you want private.

## Building and testing

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\scripts\build.ps1 -Arch x64 -SkipGeo     # quick local build without the 127 MB geo database
.\out\publish-win-x64\SurfaceInsight.exe --self-test
Get-Content .\out\publish-win-x64\self-test.txt
```

Every line should say `PASS`. On a PC without a battery use `--self-test --ci`. If you change collection or aggregation, add a check to `src/SurfaceInsight/Diagnostics.cs`. Checks run in CI on every pull request.

You can compile-check on Linux or macOS with `dotnet build -p:EnableWindowsTargeting=true`, but the app itself only runs on Windows.

## Pull requests

- Keep a pull request to one change, and say what you tested and on which architecture.
- Match the surrounding style. The UI code is written compactly, so look at neighbouring code first.
- The app is deliberately **local-only**: no analytics, no uploads, no automatic system changes. Changes that add any of those won't be accepted.
- Measurement wording matters. If a number can be misread (for example, whole-device watts shown next to an app), say what it is and isn't.
- Update `CHANGELOG.md` and, for user-visible changes, `docs/Read-Me.md`.

## Releases

Maintainers bump `<Version>` in `src/SurfaceInsight/SurfaceInsight.csproj` and push a `vX.Y.Z` tag. See the README.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
