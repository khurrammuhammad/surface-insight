<#
  Installs or updates Surface Insight from the latest GitHub release, choosing the build that matches this laptop (x64 or Arm64).
  One-time setup: winget install GitHub.cli ; gh auth login
  Then:           .\install.ps1            (re-run any time to update)
  Your history in %LOCALAPPDATA%\SurfaceInsight is never touched.
#>
param([string]$Repo = 'khurrammuhammad/surface-insight', [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\SurfaceInsight'), [string]$Tag = '')
$ErrorActionPreference = 'Stop'

# Real machine architecture, even when this PowerShell is itself running under emulation.
Add-Type -Namespace Native -Name Wow -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsWow64Process2(System.IntPtr process, out ushort processMachine, out ushort nativeMachine);'
[uint16]$pm = 0; [uint16]$nm = 0
$arch = if ([Native.Wow]::IsWow64Process2([Diagnostics.Process]::GetCurrentProcess().Handle, [ref]$pm, [ref]$nm)) { switch ($nm) { 0xAA64 { 'arm64' } 0x8664 { 'x64' } default { throw ("Unsupported processor (machine type 0x{0:X4}). Surface Insight supports Windows x64 and Arm64." -f $nm) } } }
         else { if ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64' -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' } }
Write-Host "This laptop: Windows $arch"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI not found. Run: winget install GitHub.cli   then: gh auth login' }
gh auth status 2>$null | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Not signed in to GitHub. Run: gh auth login' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('SurfaceInsight-install-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $tmp | Out-Null
try {
    $args2 = @('release', 'download'); if ($Tag) { $args2 += $Tag }
    $args2 += @('--repo', $Repo, '--pattern', "SurfaceInsight-*-win-$arch.zip", '--dir', $tmp)
    Write-Host "Downloading the latest $arch release from $Repo ..."; & gh @args2; if ($LASTEXITCODE -ne 0) { throw 'Download failed. Check the repository name and that you have access to it.' }
    $zip = Get-ChildItem $tmp -Filter '*.zip' | Select-Object -First 1; if (-not $zip) { throw "No $arch package found in the release." }
    Write-Host "Got $($zip.Name)"

    $running = Get-Process SurfaceInsight -ErrorAction SilentlyContinue
    if ($running) {
        $ans = Read-Host 'Surface Insight is running. Stop it to update? Monitoring pauses briefly. (y/n)'; if ($ans -notmatch '^[Yy]') { throw 'Cancelled. Nothing was changed.' }
        $running | Stop-Process -Force; Start-Sleep -Seconds 2
    }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Expand-Archive $zip.FullName -DestinationPath $InstallDir -Force
    Get-ChildItem $InstallDir -Recurse -File | Unblock-File

    $exe = Join-Path $InstallDir 'SurfaceInsight.exe'; if (-not (Test-Path $exe)) { throw 'The package did not contain SurfaceInsight.exe.' }
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Insight.lnk'
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.IconLocation = "$exe,0"; $s.Save()
    Write-Host "Installed to $InstallDir. Start menu shortcut: Surface Insight."
    Start-Process $exe
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
