<#
  Builds self-contained Surface Insight packages for Windows x64 and/or Windows Arm64.
    .\scripts\build.ps1 -Arch all          # both, into .\dist
    .\scripts\build.ps1 -Arch arm64 -SkipGeo
  Needs the .NET 10 SDK (set $env:DOTNET to use a specific dotnet.exe). Output: out\publish-win-<arch>\ and dist\SurfaceInsight-<version>-win-<arch>.zip
#>
param([ValidateSet('x64', 'arm64', 'all')][string]$Arch = 'all', [switch]$SkipGeo)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$proj = Join-Path $repo 'src\SurfaceInsight\SurfaceInsight.csproj'
$dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the project file.' }
if (-not $SkipGeo) { & (Join-Path $PSScriptRoot 'fetch-geo.ps1') }

function Get-PeMachine([string]$file) { $b = [IO.File]::ReadAllBytes($file); $pe = [BitConverter]::ToInt32($b, 0x3C); [BitConverter]::ToUInt16($b, $pe + 4) }
$machine = @{ x64 = 0x8664; arm64 = 0xAA64 }; $nativeDir = @{ x64 = 'amd64'; arm64 = 'arm64' }
$archs = if ($Arch -eq 'all') { 'x64', 'arm64' } else { , $Arch }
New-Item -ItemType Directory -Force (Join-Path $repo 'dist') | Out-Null

foreach ($a in $archs) {
    $rid = "win-$a"; $out = Join-Path $repo "out\publish-$rid"
    Write-Host "== Publishing $rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $proj -c Release -r $rid --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    # The package must really be for that architecture, including native dependencies.
    foreach ($f in 'SurfaceInsight.exe', 'e_sqlite3.dll', 'coreclr.dll', 'PresentationNative_cor3.dll') {
        $p = Join-Path $out $f; if (-not (Test-Path $p)) { throw "$rid is missing $f" }
        if ((Get-PeMachine $p) -ne $machine[$a]) { throw "$rid : $f has the wrong machine type 0x$('{0:X4}' -f (Get-PeMachine $p))" }
    }
    $nd = Join-Path $out $nativeDir[$a]
    foreach ($f in 'KernelTraceControl.dll', 'msdia140.dll') { if (-not (Test-Path (Join-Path $nd $f))) { throw "$rid is missing $($nativeDir[$a])\$f" } }
    if (-not (Test-Path (Join-Path $out 'wpt\perfcore.dll'))) { throw "$rid is missing wpt\perfcore.dll" }
    $geo = Test-Path (Join-Path $out 'Assets\dbip-city-lite.mmdb'); if (-not $geo) { Write-Warning "$rid package has no geo database (city locations will be unavailable)." }

    $zip = Join-Path $repo "dist\SurfaceInsight-$version-$rid.zip"; if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("   OK  {0}  ({1} MB)" -f $zip, [int]((Get-Item $zip).Length / 1MB))
}
Write-Host "Done. Version $version."
