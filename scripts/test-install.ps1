# Offline regressions for the same functions used by the public installer.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install.ps1') -FunctionsOnly
$root = Join-Path ([IO.Path]::GetTempPath()) ('si-install-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$count = 0
function Assert([bool]$Ok, [string]$Name) {
    if (!$Ok) { throw "FAIL $Name" }
    $script:count++
    Write-Host "PASS $Name"
}
function Assert-Throws([scriptblock]$Action, [string]$Name) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert $threw $Name
}
function Write-TestInventory([string]$Folder, [string[]]$Names = @('SurfaceInsight.exe')) {
    $files = [ordered]@{}
    foreach ($name in $Names) { $files[$name] = (Get-FileHash -LiteralPath (Join-Path $Folder $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    [ordered]@{ Application = 'SurfaceInsight'; Schema = 1; Files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Folder 'surface-insight.files.json') -Encoding UTF8
}
try {
    $hash = 'a' * 64
    Assert ((Get-RequiredChecksum "$hash  app.zip" 'app.zip') -eq $hash) 'Valid checksum accepted'
    Assert-Throws { Get-RequiredChecksum '' 'app.zip' } 'Missing checksum refused'
    Assert-Throws { Get-RequiredChecksum "$hash  app.zip`n$hash  app.zip" 'app.zip' } 'Duplicate checksum refused'
    Assert-Throws { Get-RequiredChecksum "$('g' * 64)  app.zip" 'app.zip' } 'Malformed checksum refused'
    foreach ($name in '../outside', '/absolute', 'C:/outside', 'Assets/../../outside', 'CON.txt', 'file:stream') {
        Assert-Throws { Get-SafeFile $root $name } "Unsafe path refused: $name"
    }
    $stage = Join-Path $root 'stage'; $target = Join-Path $root 'App'; $work = Join-Path $root 'work'
    New-Item -ItemType Directory $stage, $target, $work | Out-Null
    Set-Content -LiteralPath (Join-Path $stage 'SurfaceInsight.exe') -Value new
    Set-Content -LiteralPath (Join-Path $target 'SurfaceInsight.exe') -Value old
    Set-Content -LiteralPath (Join-Path $target 'personal.txt') -Value keep
    Write-TestInventory $stage; Write-TestInventory $target
    $old = $target + '.old'; New-Item -ItemType Directory $old | Out-Null
    Set-Content -LiteralPath (Join-Path $old 'unrelated.txt') -Value keep
    $backup = Install-Inventory $target $stage $work
    Assert ((Get-Content -LiteralPath (Join-Path $target 'SurfaceInsight.exe')).Trim() -eq 'new') 'Inventoried application replaced'
    Assert ((Get-Content -LiteralPath (Join-Path $target 'personal.txt')).Trim() -eq 'keep') 'Unrelated installation file preserved'
    Assert ((Get-Content -LiteralPath (Join-Path $old 'unrelated.txt')).Trim() -eq 'keep') 'Unrelated sibling backup preserved'
    Restore-Installation $target $backup
    Assert ((Get-Content -LiteralPath (Join-Path $target 'SurfaceInsight.exe')).Trim() -eq 'old') 'Recovery restores old executable'
    Set-Content -LiteralPath (Join-Path $backup 'unknown.txt') -Value keep
    Complete-Installation $backup
    Assert (Test-Path -LiteralPath (Join-Path $backup 'unknown.txt')) 'Cleanup preserves unknown transaction files'
    Set-Content -LiteralPath (Join-Path $stage 'SurfaceInsight.exe') -Value tampered
    Assert-Throws { Install-Inventory $target $stage $work } 'Changed package refused before modifying target'
    Assert ((Get-Content -LiteralPath (Join-Path $target 'SurfaceInsight.exe')).Trim() -eq 'old') 'Failed verification leaves installation intact'
    Write-TestInventory $stage
    Set-Content -LiteralPath (Join-Path $stage 'personal.txt') -Value replacement
    Write-TestInventory $stage @('SurfaceInsight.exe','personal.txt')
    Assert-Throws { Install-Inventory $target $stage $work } 'Package cannot overwrite a previously unowned file'
    Remove-Item -LiteralPath (Join-Path $target 'surface-insight.files.json')
    Assert-Throws { Install-Inventory $target $stage $work } 'Legacy unowned destination requires safe migration'
    Write-Host "$count installer checks passed."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
