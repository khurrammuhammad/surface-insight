<#
Installs/updates Surface Insight using a mandatory checksum and a file inventory.
Use a dedicated folder. -Uninstall preserves unknown/modified files and history.
#>
param(
    [string]$Repo = 'khurrammuhammad/surface-insight',
    [string]$InstallDir = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\SurfaceInsight' } else { '' }),
    [string]$Tag = '',
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$Force,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'
$manifestName = 'surface-insight.files.json'

function Assert-NoLinks([string]$Path) {
    $p = [IO.Path]::GetFullPath($Path)
    while ($p) {
        if (Test-Path -LiteralPath $p) {
            if ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked/reparse paths are not supported: $p" }
        }
        $p = [IO.Path]::GetDirectoryName($p)
    }
}
function Assert-NoLinksTree([string]$Path) {
    Assert-NoLinks $Path
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing recursive removal through a linked child: $($item.FullName)" }
        if ($item.PSIsContainer) { Assert-NoLinksTree $item.FullName }
    }
}
function Get-SafeRoot([string]$Path) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
    if (!$p -or $p -eq [IO.Path]::GetPathRoot($p).TrimEnd([char[]]'\/')) { throw 'A drive root cannot be an installation folder.' }
    Assert-NoLinks $p
    return $p
}
function Get-SafeFile([string]$Root, [string]$Name) {
    $parts = $Name.Replace('\', '/').Split('/')
    foreach ($part in $parts) {
        if (!$part -or $part -in '.', '..' -or $part -notmatch '^[A-Za-z0-9_. -]+$' -or $part -match '[. ]$' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw "Unsafe package path: $Name" }
    }
    $p = [IO.Path]::GetFullPath((Join-Path $Root ($parts -join [IO.Path]::DirectorySeparatorChar)))
    if (!$p.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes its folder.' }
    Assert-NoLinks $p
    return $p
}
function Read-Inventory([string]$Root, [switch]$Verify) {
    $Root = Get-SafeRoot $Root
    $m = Get-Content -LiteralPath (Get-SafeFile $Root $manifestName) -Raw | ConvertFrom-Json
    if ($m.Application -ne 'SurfaceInsight' -or $m.Schema -ne 1 -or !$m.Files) { throw 'Invalid Surface Insight inventory.' }
    $files = @{}
    foreach ($entry in $m.Files.PSObject.Properties) {
        $p = Get-SafeFile $Root $entry.Name
        if ($files.ContainsKey($entry.Name) -or $entry.Name -eq $manifestName -or $entry.Value -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid or duplicate inventory entry.' }
        if ($Verify -and (!(Test-Path -LiteralPath $p -PathType Leaf) -or (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $entry.Value)) { throw "Inventory verification failed: $($entry.Name)" }
        $files[$entry.Name] = $entry.Value.ToLowerInvariant()
    }
    if (!$files.ContainsKey('SurfaceInsight.exe') -or $files.Count -gt 10000) { throw 'Missing application executable in inventory.' }
    return $files
}
function Get-RequiredChecksum([string]$Text, [string]$Name) {
    $found = @($Text -split '\r?\n' | Where-Object { $_.Trim() -match ('\s+\*?' + [regex]::Escape($Name) + '$') })
    if ($found.Count -ne 1 -or $found[0].Trim() -notmatch '^([a-fA-F0-9]{64})\s+\*?.+$') { throw "A unique valid SHA-256 checksum is required for $Name. Nothing was installed." }
    return $Matches[1].ToLowerInvariant()
}
function Get-NativeArchitecture {
    if (!('SurfaceInsightInstaller.NativeArchitecture' -as [type])) {
        Add-Type -Namespace SurfaceInsightInstaller -Name NativeArchitecture -MemberDefinition '[DllImport("kernel32.dll", SetLastError=true)] public static extern bool IsWow64Process2(System.IntPtr process, out ushort processMachine, out ushort nativeMachine);'
    }
    [uint16]$processMachine = 0; [uint16]$nativeMachine = 0
    if (![SurfaceInsightInstaller.NativeArchitecture]::IsWow64Process2([Diagnostics.Process]::GetCurrentProcess().Handle, [ref]$processMachine, [ref]$nativeMachine)) { throw 'Windows could not determine the native architecture.' }
    switch ($nativeMachine) {
        0xAA64 { return 'arm64' }
        0x8664 { return 'x64' }
        default { throw 'Surface Insight requires Windows 11 x64 or Arm64.' }
    }
}
function Stop-Installation([string]$Root) {
    $exe = Join-Path $Root 'SurfaceInsight.exe'
    $running = @(Get-Process SurfaceInsight -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exe } catch { $false } })
    if (!$running) { return }
    if (!$Force -and (Read-Host 'Close this Surface Insight installation to continue? (y/n)') -notmatch '^[Yy]$') { throw 'Cancelled. Nothing was changed.' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $id = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.TrimEnd([char[]]'\/').ToUpperInvariant())))).Replace('-', '').Substring(0, 24) } finally { $sha.Dispose() }
    try { $quit = [Threading.EventWaitHandle]::OpenExisting("Local\SurfaceInsight.Quit.$id"); try { $quit.Set() | Out-Null } finally { $quit.Dispose() } } catch { }
    foreach ($p in $running) { $p.CloseMainWindow() | Out-Null }
    $deadline = (Get-Date).AddSeconds(15)
    do { $left = @($running | Where-Object { !$_.HasExited }); if (!$left) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
    foreach ($p in $left) { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } }
}
function Restore-Installation([string]$Root, [string]$Backup) {
    $j = Get-Content -LiteralPath (Join-Path $Backup 'transaction.json') -Raw | ConvertFrom-Json
    if ($j.Application -ne 'SurfaceInsight' -or $j.Target -ne (Get-SafeRoot $Root)) { throw 'Recovery journal does not belong to the installation.' }
    $reverse = @($j.Entries); [array]::Reverse($reverse)
    foreach ($e in $reverse) {
        $dest = Get-SafeFile $Root $e.Name
        $current = if (Test-Path -LiteralPath $dest -PathType Leaf) { (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        if ($current -eq $e.Before) { continue }
        if ($current -ne $e.After) { throw "Recovery stopped to preserve a subsequently modified file: $dest. Backup: $Backup" }
        if ($e.Before) {
            $src = Get-SafeFile $Backup $e.Backup
            if ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -ne $e.Before) { throw 'Recovery backup hash mismatch.' }
            Copy-Item -LiteralPath $src -Destination $dest -Force
        } elseif (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest }
    }
}
function Complete-Installation([string]$Backup) {
    $j = Get-Content -LiteralPath (Join-Path $Backup 'transaction.json') -Raw | ConvertFrom-Json
    if ($j.Application -ne 'SurfaceInsight') { throw 'Invalid recovery journal.' }
    foreach ($e in $j.Entries) { if ($e.Before) { Remove-Item -LiteralPath (Get-SafeFile $Backup $e.Backup) } }
    foreach ($name in 'healthy', 'transaction.json') { $p = Get-SafeFile $Backup $name; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p } }
    if (!@(Get-ChildItem -LiteralPath $Backup -Force).Count) { Remove-Item -LiteralPath $Backup }
}
function Install-Inventory([string]$Root, [string]$Stage, [string]$BackupRoot) {
    $Root = Get-SafeRoot $Root
    $new = Read-Inventory $Stage -Verify
    $old = @{}
    if (Test-Path -LiteralPath (Join-Path $Root $manifestName)) { $old = Read-Inventory $Root }
    elseif ((Test-Path -LiteralPath $Root) -and @(Get-ChildItem -LiteralPath $Root -Force).Count) {
        throw 'The destination has no ownership inventory. Use a new empty dedicated folder, or upgrade v1.1.0 through its in-app updater. Existing files were kept.'
    }
    $new[$manifestName] = (Get-FileHash -LiteralPath (Join-Path $Stage $manifestName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $entries = @()
    foreach ($name in @(@($new.Keys) + @($old.Keys) | Sort-Object -Unique)) {
        $dest = Get-SafeFile $Root $name
        $before = if (Test-Path -LiteralPath $dest -PathType Leaf) { (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        if (!$new.ContainsKey($name) -and (!$before -or $before -ne $old[$name])) { continue }
        if ($new.ContainsKey($name) -and $before -and !$old.ContainsKey($name) -and $name -ne $manifestName) { throw "Refusing to overwrite an unowned file: $name" }
        $entries += [pscustomobject]@{ Name = $name; Before = $before; After = $new[$name]; Backup = ('{0:D5}.bak' -f $entries.Count) }
    }
    $backup = Join-Path (Get-SafeRoot $BackupRoot) ('transaction-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($e in $entries) { if ($e.Before) { Copy-Item -LiteralPath (Get-SafeFile $Root $e.Name) -Destination (Join-Path $backup $e.Backup) } }
    [pscustomobject]@{ Application = 'SurfaceInsight'; Target = $Root; Token = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')); Entries = $entries } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'transaction.json') -Encoding UTF8
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    try {
        foreach ($e in $entries) {
            $dest = Get-SafeFile $Root $e.Name
            $current = if (Test-Path -LiteralPath $dest -PathType Leaf) { (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
            if ($current -ne $e.Before) { throw "File changed during installation: $($e.Name)" }
            if (!$e.After) { Remove-Item -LiteralPath $dest; continue }
            $src = Get-SafeFile $Stage $e.Name
            if ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -ne $e.After) { throw 'Staging changed during installation.' }
            New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
            $temp = $dest + '.si-' + [guid]::NewGuid().ToString('N') + '.tmp'
            try {
                Copy-Item -LiteralPath $src -Destination $temp
                if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $e.After) { throw 'Replacement verification failed.' }
                Assert-NoLinks $dest
                if (Test-Path -LiteralPath $dest) { [IO.File]::Replace($temp, $dest, [System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp, $dest) }
            } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
        }
        return $backup
    } catch {
        $failure = $_
        Restore-Installation $Root $backup
        throw "Installation failed; previous files restored. Recovery journal: $backup. $failure"
    }
}
if ($FunctionsOnly) { return }
if ($env:OS -ne 'Windows_NT') { throw 'Surface Insight supports Windows 11 only.' }
$InstallDir = Get-SafeRoot $InstallDir
$exe = Join-Path $InstallDir 'SurfaceInsight.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'SurfaceInsight'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Insight.lnk'
$work = Join-Path $dataDir 'updates'
Assert-NoLinks $work
New-Item -ItemType Directory -Path $work -Force | Out-Null
if ($Uninstall) {
    $owned = Read-Inventory $InstallDir
    Stop-Installation $InstallDir
    foreach ($name in $owned.Keys) {
        $p = Get-SafeFile $InstallDir $name
        if ((Test-Path -LiteralPath $p -PathType Leaf) -and (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -eq $owned[$name]) { Remove-Item -LiteralPath $p }
        elseif (Test-Path -LiteralPath $p) { Write-Warning "Kept modified file: $p" }
    }
    Remove-Item -LiteralPath (Join-Path $InstallDir $manifestName)
    if (Test-Path -LiteralPath $shortcut) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
        if ($s.TargetPath -eq $exe) { Remove-Item -LiteralPath $shortcut }
    }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $run = (Get-ItemProperty -Path $runKey -Name SurfaceInsight -ErrorAction SilentlyContinue).SurfaceInsight
    if ($run -and $run.Contains($exe)) { Remove-ItemProperty -Path $runKey -Name SurfaceInsight }
    if ($RemoveData -and (Test-Path -LiteralPath $dataDir)) {
        Assert-NoLinksTree $dataDir
        if ($Force -or (Read-Host 'Permanently delete ALL Surface Insight history and diagnostic captures? (y/n)') -match '^[Yy]$') { Remove-Item -LiteralPath $dataDir -Recurse -Force }
    }
    Write-Host 'Owned unmodified application files removed. Unknown files and history were kept unless explicitly deleted.'
    return
}
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
$arch = Get-NativeArchitecture
$tmp = Join-Path $work ('download-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$oldProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'
$backup = $null
try {
    $api = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$([Uri]::EscapeDataString($Tag))" } else { "https://api.github.com/repos/$Repo/releases/latest" }
    $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'SurfaceInsight-installer' } -UseBasicParsing
    $asset = @($release.assets | Where-Object { $_.name -match "^SurfaceInsight-(.+-)?win-$arch\.zip$" })
    $sums = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' })
    if ($asset.Count -ne 1 -or $sums.Count -ne 1) { throw 'Release must contain one matching ZIP and SHA256SUMS.txt. Nothing was installed.' }
    $text = (Invoke-WebRequest -Uri $sums[0].browser_download_url -UseBasicParsing).Content
    if ($text -is [byte[]]) { $text = [Text.Encoding]::ASCII.GetString($text) }
    $want = Get-RequiredChecksum $text $asset[0].name
    $zip = Join-Path $tmp 'package.zip'
    Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile $zip -UseBasicParsing
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $want) { throw 'Checksum mismatch. Nothing was installed.' }
    $stage = Join-Path $tmp 'package'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage
    Read-Inventory $stage -Verify | Out-Null
    if ((Test-Path -LiteralPath $InstallDir) -and @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -and !(Test-Path -LiteralPath (Join-Path $InstallDir $manifestName))) {
        throw 'This legacy destination has no file inventory. Use a new empty dedicated folder; the running old copy and its files were kept.'
    }
    Stop-Installation $InstallDir
    $backup = Install-Inventory $InstallDir $stage $work
    # This also exercises SQLite, bundled native libraries and update recovery helpers before launch.
    $test = Start-Process -FilePath $exe -ArgumentList '--self-test','--ci' -PassThru
    if (!$test.WaitForExit(120000)) { $test.Kill(); throw 'Installed self-test timed out. Recovery backup retained.' }
    if ($test.ExitCode -ne 0) { throw 'Installed self-test failed. Recovery backup retained.' }
    $inventory = Read-Inventory $InstallDir
    foreach ($name in $inventory.Keys) { Unblock-File -LiteralPath (Get-SafeFile $InstallDir $name) }
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.IconLocation = "$exe,0"; $s.Save()
    $journal = Get-Content -LiteralPath (Join-Path $backup 'transaction.json') -Raw | ConvertFrom-Json
    $health = Join-Path $backup 'healthy'
    $app = Start-Process -FilePath $exe -WorkingDirectory $InstallDir -ArgumentList @('--update-health', ('"' + $health + '"'), $journal.Token) -PassThru
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    do {
        if ((Test-Path -LiteralPath $health) -and (Get-Content -LiteralPath $health -Raw) -eq $journal.Token) { $healthy = $true; break }
        if ($app.HasExited) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (!$healthy) { throw 'The dashboard and collector did not acknowledge a healthy startup.' }
    try { Complete-Installation $backup } catch { Write-Warning "Installation is healthy; backup cleanup needs attention: $($_.Exception.Message)" }
    $backup = $null
    Write-Host "Installed $($release.tag_name). Your history and unknown files were kept."
} catch {
    $failure = $_
    if ($backup) {
        try {
            Stop-Installation $InstallDir
            Restore-Installation $InstallDir $backup
            if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -WorkingDirectory $InstallDir }
        } catch { throw "Recovery needs attention: $($_.Exception.Message). Keep the journal/backups at $backup. Original failure: $failure" }
    }
    throw "Installation could not complete: $failure. Existing history was kept. Recovery backup: $backup. Manual downloads: https://github.com/$Repo/releases"
} finally {
    $ProgressPreference = $oldProgress
    # This unique temporary directory was created by this invocation, not derived from an install destination.
    if (Test-Path -LiteralPath $tmp) { Assert-NoLinksTree $tmp; Remove-Item -LiteralPath $tmp -Recurse -Force }
}
