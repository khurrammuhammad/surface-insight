<#
  Builds self-contained Surface Insight packages for Windows x64 and/or Windows Arm64.
    .\scripts\build.ps1 -Arch all          # both, into .\dist
    .\scripts\build.ps1 -Arch arm64 -SkipGeo
    .\scripts\build.ps1 -Arch all -SingleFile   # also a single self-extracting SurfaceInsight.exe per architecture (experimental)
  Needs the .NET 10 SDK (set $env:DOTNET to use a specific dotnet.exe). Output: out\publish-win-<arch>\ and dist\SurfaceInsight-<version>-win-<arch>.zip
#>
param([ValidateSet('x64', 'arm64', 'all')][string]$Arch = 'all', [switch]$SkipGeo, [switch]$SingleFile, [switch]$EnableWindowsTargeting)
$ErrorActionPreference = 'Stop'
[string[]]$targeting = @()
if ($EnableWindowsTargeting) { $targeting = @('-p:EnableWindowsTargeting=true') }
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
    & $dotnet publish $proj -c Release -r $rid --self-contained true -o $out @targeting
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

    $commit = (& git -C $repo rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record build commit.' }
    $dirty = @(& git -C $repo status --porcelain).Count -gt 0
    $dependencies = (Get-Content (Join-Path $out 'SurfaceInsight.deps.json') -Raw | ConvertFrom-Json).libraries.PSObject.Properties | ForEach-Object { $_.Name }
    [ordered]@{ application = 'SurfaceInsight'; version = $version; commit = $commit; dirty = $dirty; runtime = $rid; sdk = (& $dotnet --version); dependencies = @($dependencies) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'build-info.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'write-inventory.ps1') -Root $out

    $zip = Join-Path $repo "dist\SurfaceInsight-$version-$rid.zip"; if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("   OK  {0}  ({1} MB)" -f $zip, [int]((Get-Item $zip).Length / 1MB))

    if ($SingleFile) {
        try {
            # One self-extracting exe: managed code, native libraries and Assets are bundled and unpacked to a per-user cache on first run.
            $sfOut = Join-Path $repo "out\publish-single-$rid"; Write-Host "== Publishing $rid (single file)"
            if (Test-Path $sfOut) { Remove-Item $sfOut -Recurse -Force }
            & $dotnet publish $proj -c Release -r $rid --self-contained true -o $sfOut @targeting -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish (single file) failed for $rid" }
            $exe = Join-Path $sfOut 'SurfaceInsight.exe'; if (-not (Test-Path $exe)) { throw "$rid single-file build has no SurfaceInsight.exe" }
            if ((Get-PeMachine $exe) -ne $machine[$a]) { throw "$rid single-file exe has the wrong machine type 0x$('{0:X4}' -f (Get-PeMachine $exe))" }
            $exeCopy = Join-Path $repo "dist\SurfaceInsight-$version-$rid.exe"; Copy-Item $exe $exeCopy -Force
            Write-Host ("   OK  {0}  ({1} MB)" -f $exeCopy, [int]((Get-Item $exeCopy).Length / 1MB))
        } catch { throw "Single-file build for $rid failed: $($_.Exception.Message)" }
    }
}
Write-Host "Done. Version $version."
