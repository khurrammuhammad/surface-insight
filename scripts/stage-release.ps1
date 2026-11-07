<#
  Gathers the files for a GitHub release into dist\release with stable names, so the "latest" download links never change:
    SurfaceInsight-win-x64.zip, SurfaceInsight-win-arm64.zip, (optionally) SurfaceInsight-win-x64.exe, SurfaceInsight-win-arm64.exe, SHA256SUMS.txt
  Run after scripts\build.ps1. Use -NoExe to leave the single-file executables out (for example when their self-test failed).
#>
param([switch]$NoExe)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$proj = Join-Path $repo 'src\SurfaceInsight\SurfaceInsight.csproj'
$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $repo 'dist'; $rel = Join-Path $dist 'release'
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
New-Item -ItemType Directory $rel | Out-Null
foreach ($a in 'x64', 'arm64') {
    $zip = Join-Path $dist "SurfaceInsight-$version-win-$a.zip"; if (-not (Test-Path $zip)) { throw "Missing $zip. Run scripts\build.ps1 -Arch all first." }
    Copy-Item $zip (Join-Path $rel "SurfaceInsight-win-$a.zip")
    $exe = Join-Path $dist "SurfaceInsight-$version-win-$a.exe"
    if (-not $NoExe -and (Test-Path $exe)) { Copy-Item $exe (Join-Path $rel "SurfaceInsight-win-$a.exe") }
}
# sha256sum format ("<hash>  <file>"), so both `sha256sum -c` and the installer can read it.
Get-ChildItem $rel -File | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } | Set-Content (Join-Path $rel 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem $rel -File | ForEach-Object { Write-Host ("{0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
