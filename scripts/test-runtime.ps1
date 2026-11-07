# Windows-only startup/update smoke test. Uses isolated application/data folders and never elevates.
param([Parameter(Mandatory)][string]$Publish)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Runtime smoke tests require Windows.' }
. (Join-Path $PSScriptRoot 'install.ps1') -FunctionsOnly
$source = [IO.Path]::GetFullPath($Publish)
$root = Join-Path ([IO.Path]::GetTempPath()) ('si-runtime-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $root 'App'; $stage = Join-Path $root 'staging'
$data = Join-Path $root 'data'
$priorData = $env:SURFACEINSIGHT_DATA
$Force = $true
try {
    New-Item -ItemType Directory $target, $stage, $data -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse
    Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse
    Set-Content -LiteralPath (Join-Path $target 'personal.txt') -Value keep
    New-Item -ItemType Directory ($target + '.old') | Out-Null
    Set-Content -LiteralPath (Join-Path ($target + '.old') 'unrelated.txt') -Value keep
    $env:SURFACEINSIGHT_DATA = $data
    $exe = Join-Path $target 'SurfaceInsight.exe'
    Start-Process -FilePath $exe -WorkingDirectory $target | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    $status = Join-Path $data 'collector.status'
    $collected = $false
    do {
        if ((Test-Path -LiteralPath $status) -and @(Get-Content -LiteralPath $status | Where-Object { $_ -match '^ok=\d' }).Count) { $collected = $true; break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if (!$collected) { throw 'Fresh dashboard/collector did not start and collect a sample.' }
    Write-Host 'PASS isolated fresh dashboard and collector startup'
    $arguments = '--apply-update "' + $target + '" "' + $stage + '" 0'
    $applier = Start-Process -FilePath (Join-Path $stage 'SurfaceInsight.exe') -ArgumentList $arguments -WorkingDirectory $stage -PassThru
    if (!$applier.WaitForExit(180000)) { $applier.Kill(); throw 'End-to-end replacement timed out.' }
    $result = Join-Path $data 'update.result'
    if (!(Test-Path -LiteralPath $result) -or (Get-Content -LiteralPath $result -Raw) -notmatch '^ok\|') { throw 'Replacement did not report healthy startup.' }
    if ((Get-Content -LiteralPath (Join-Path $target 'personal.txt')).Trim() -ne 'keep' -or (Get-Content -LiteralPath (Join-Path ($target + '.old') 'unrelated.txt')).Trim() -ne 'keep') { throw 'Unrelated user files were changed.' }
    Write-Host 'PASS end-to-end replacement, startup acknowledgement and unrelated-file preservation'
} finally {
    if (Test-Path -LiteralPath $target) { Stop-Installation $target }
    $env:SURFACEINSIGHT_DATA = $priorData
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
