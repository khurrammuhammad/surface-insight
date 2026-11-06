<#
  Downloads the DB-IP "IP to City Lite" database (CC BY 4.0, https://db-ip.com) into the app's Assets folder.
  The file is about 127 MB, so it is not committed to git; the build and the release zips include it.
  The map and city names work without it, but every address is then shown without a location.
#>
param([string]$Dest = (Join-Path $PSScriptRoot '..\src\SurfaceInsight\Assets\dbip-city-lite.mmdb'), [switch]$Force)
$ErrorActionPreference = 'Stop'
$Dest = [IO.Path]::GetFullPath($Dest)
if ((Test-Path $Dest) -and -not $Force) { Write-Host "Geo database already present: $Dest"; return }
New-Item -ItemType Directory -Force (Split-Path $Dest) | Out-Null
$ProgressPreference = 'SilentlyContinue'
# DB-IP publishes one file per month; the current month appears a few days late, so fall back to earlier months.
foreach ($back in 0..2) {
    $month = (Get-Date).ToUniversalTime().AddMonths(-$back).ToString('yyyy-MM')
    $url = "https://download.db-ip.com/free/dbip-city-lite-$month.mmdb.gz"
    $gz = Join-Path ([IO.Path]::GetTempPath()) "dbip-city-lite-$month.mmdb.gz"
    try {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $gz -UseBasicParsing
        $in = [IO.File]::OpenRead($gz); $gzs = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress); $out = [IO.File]::Create($Dest)
        try { $gzs.CopyTo($out) } finally { $out.Dispose(); $gzs.Dispose(); $in.Dispose(); Remove-Item $gz -ErrorAction SilentlyContinue }
        if ((Get-Item $Dest).Length -lt 10MB) { throw "Downloaded file is too small to be the database." }
        Write-Host "Saved $Dest ($([int]((Get-Item $Dest).Length / 1MB)) MB, $month edition)"
        return
    } catch { Write-Warning "$month edition not available: $($_.Exception.Message)"; Remove-Item $Dest -ErrorAction SilentlyContinue }
}
throw "Could not download the DB-IP City Lite database. The app still builds and runs without it (no city locations)."
