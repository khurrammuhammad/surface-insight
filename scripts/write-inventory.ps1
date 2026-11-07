param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$files = [ordered]@{}
Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish output must not contain linked files.' }
    $name = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
    if ($name -notin 'surface-insight.files.json', 'self-test.txt') { $files[$name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
if (!$files.Contains('SurfaceInsight.exe')) { throw 'Publish output has no SurfaceInsight.exe.' }
[ordered]@{ Application = 'SurfaceInsight'; Schema = 1; Files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root 'surface-insight.files.json') -Encoding utf8
