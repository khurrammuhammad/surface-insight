<#
  Installs, updates or removes Surface Insight. Picks the build that matches this PC (x64 or Arm64) from the latest GitHub release.

  Install or update (PowerShell, no admin rights needed):
      irm https://raw.githubusercontent.com/khurrammuhammad/surface-insight/main/scripts/install.ps1 | iex

  With options, download the script first:
      irm https://raw.githubusercontent.com/khurrammuhammad/surface-insight/main/scripts/install.ps1 -OutFile install.ps1
      .\install.ps1                 # install or update (your history is kept)
      .\install.ps1 -Tag v1.1.0     # a specific release
      .\install.ps1 -Uninstall      # remove the program and shortcut, keep history
      .\install.ps1 -Uninstall -RemoveData   # also delete history in %LOCALAPPDATA%\SurfaceInsight

  Re-running the install command updates in place. The download is verified against the release's SHA256SUMS.txt.
#>
param(
    [string]$Repo = 'khurrammuhammad/surface-insight',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\SurfaceInsight'),
    [string]$Tag = '',
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

if ($env:OS -ne 'Windows_NT') { throw 'Surface Insight runs on Windows 11 only.' }
$releasesPage = "https://github.com/$Repo/releases"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Insight.lnk'
$dataDir = Join-Path $env:LOCALAPPDATA 'SurfaceInsight'

function Stop-SurfaceInsight {
    $running = Get-Process SurfaceInsight -ErrorAction SilentlyContinue
    if (-not $running) { return }
    if (-not $Force) {
        $ans = Read-Host 'Surface Insight is running. Close it to continue? Monitoring pauses briefly. (y/n)'
        if ($ans -notmatch '^[Yy]') { throw 'Cancelled. Nothing was changed.' }
    }
    $running | Stop-Process -Force; Start-Sleep -Seconds 2
}

if ($Uninstall) {
    Stop-SurfaceInsight
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" } else { Write-Host "Nothing installed at $InstallDir" }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force; Write-Host 'Removed the Start menu shortcut.' }
    try { Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SurfaceInsight' -ErrorAction SilentlyContinue } catch { }
    if ($RemoveData -and (Test-Path $dataDir)) {
        $ok = $Force; if (-not $ok) { $ok = (Read-Host "Delete your history in $dataDir ? This cannot be undone. (y/n)") -match '^[Yy]' }
        if ($ok) { Remove-Item $dataDir -Recurse -Force; Write-Host "Deleted $dataDir" } else { Write-Host 'History kept.' }
    } elseif (Test-Path $dataDir) { Write-Host "Your history is kept in $dataDir (use -RemoveData to delete it)." }
    Write-Host 'Done.'
}
else {
    # Real machine architecture, even when this PowerShell is itself running under emulation.
    Add-Type -Namespace Native -Name Wow -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsWow64Process2(System.IntPtr process, out ushort processMachine, out ushort nativeMachine);' -ErrorAction SilentlyContinue
    [uint16]$pm = 0; [uint16]$nm = 0
    $arch = $null
    try { if ([Native.Wow]::IsWow64Process2([Diagnostics.Process]::GetCurrentProcess().Handle, [ref]$pm, [ref]$nm)) { $arch = switch ($nm) { 0xAA64 { 'arm64' } 0x8664 { 'x64' } default { throw ("Unsupported processor (machine type 0x{0:X4}). Surface Insight supports Windows x64 and Arm64." -f $nm) } } } } catch [System.EntryPointNotFoundException] { }
    if (-not $arch) { $arch = if ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64' -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } elseif ($env:PROCESSOR_ARCHITECTURE -eq 'x86' -and -not $env:PROCESSOR_ARCHITEW6432) { throw 'Surface Insight needs 64-bit Windows 11 (x64 or Arm64).' } else { 'x64' } }
    Write-Host "This PC: Windows $arch"

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('SurfaceInsight-install-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $tmp | Out-Null
    $oldProgress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'   # the progress bar makes Windows PowerShell 5.1 downloads very slow
    $stage = "$InstallDir.new"
    try {
        $api = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$Repo/releases/latest" }
        try { $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'SurfaceInsight-installer'; 'Accept' = 'application/vnd.github+json' } -UseBasicParsing }
        catch { throw "Could not read the release list from GitHub ($($_.Exception.Message)). Check your internet connection, or download the zip by hand from $releasesPage" }
        $asset = $release.assets | Where-Object { $_.name -match "^SurfaceInsight-(.+-)?win-$arch\.zip$" } | Select-Object -First 1
        if (-not $asset) { throw "Release $($release.tag_name) has no $arch download. See $releasesPage" }
        Write-Host ("Downloading {0} ({1:N0} MB) from release {2} ..." -f $asset.name, ($asset.size / 1MB), $release.tag_name)
        $zip = Join-Path $tmp $asset.name
        try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'SurfaceInsight-installer' } }
        catch { throw "Download failed ($($_.Exception.Message)). Try again, or download the zip by hand from $releasesPage" }

        $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
        if ($sums) {
            $text = (Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing -Headers @{ 'User-Agent' = 'SurfaceInsight-installer' }).Content
            if ($text -is [byte[]]) { $text = [Text.Encoding]::ASCII.GetString($text) }
            $line = ($text -split "`r?`n") | Where-Object { $_ -match ('\s' + [regex]::Escape($asset.name) + '$') } | Select-Object -First 1
            if (-not $line) { throw "SHA256SUMS.txt does not list $($asset.name). Nothing was installed." }
            $want = ($line -split '\s+')[0].ToLower(); $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
            if ($want -ne $got) { throw "The download does not match its published checksum (expected $want, got $got). It may be corrupt or altered. Nothing was changed." }
            Write-Host 'Checksum verified.'
        } else { Write-Warning "Release $($release.tag_name) has no SHA256SUMS.txt, so the download could not be verified." }

        # Unpack beside the install folder first, so a bad zip never breaks a working install.
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        Expand-Archive $zip -DestinationPath $stage -Force
        if (-not (Test-Path (Join-Path $stage 'SurfaceInsight.exe'))) { throw 'The package did not contain SurfaceInsight.exe. Nothing was changed.' }

        Stop-SurfaceInsight
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        Rename-Item $stage (Split-Path $InstallDir -Leaf)
        Get-ChildItem $InstallDir -Recurse -File | Unblock-File

        $exe = Join-Path $InstallDir 'SurfaceInsight.exe'
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut); $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.IconLocation = "$exe,0"; $s.Save()
        Write-Host "Installed Surface Insight $($release.tag_name) to $InstallDir (Start menu: Surface Insight)."
        Write-Host 'Run this command again any time to update. Your history is kept.'
        Start-Process $exe
    }
    finally {
        $ProgressPreference = $oldProgress
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
