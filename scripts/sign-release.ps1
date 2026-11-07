<# Called only in the protected release environment. Credentials are supplied by GitHub secrets, never checked in. #>
param([Parameter(Mandatory)][string]$Zip, [Parameter(Mandatory)][string]$SingleFile, [string]$Installer)
$ErrorActionPreference = 'Stop'
if (!$env:SIGNING_PFX_BASE64 -or !$env:SIGNING_PFX_PASSWORD) { throw 'Release signing is not configured. Set SIGNING_PFX_BASE64 and SIGNING_PFX_PASSWORD in the protected release environment; unsigned publication is refused.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('si-sign-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$pfx = Join-Path $root 'certificate.pfx'
$cert = $null
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_PFX_BASE64))
    $password = ConvertTo-SecureString $env:SIGNING_PFX_PASSWORD -AsPlainText -Force
    $pfxData = Get-PfxData -FilePath $pfx -Password $password
    if ($pfxData.EndEntityCertificates.Count -ne 1) { throw 'Provide exactly one signing certificate.' }
    if (Test-Path ("Cert:\CurrentUser\My\" + $pfxData.EndEntityCertificates[0].Thumbprint)) { throw 'Use a clean signing runner; refusing to alter an existing certificate/key.' }
    $cert = Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\CurrentUser\My -Password $password -Exportable:$false
    if (!$cert -or !$cert.HasPrivateKey) { throw 'Signing certificate has no usable private key.' }
    $package = Join-Path $root 'package'
    Expand-Archive -LiteralPath $Zip -DestinationPath $package
    $signPaths = @((Join-Path $package 'SurfaceInsight.exe'), $SingleFile)
    if ($Installer) { $signPaths += $Installer }
    foreach ($path in $signPaths) {
        $signature = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
        if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) { throw "Signing/timestamp validation failed: $path" }
        $verified = Get-AuthenticodeSignature -FilePath $path
        if ($verified.Status -ne 'Valid' -or $verified.SignerCertificate.Thumbprint -ne $cert.Thumbprint) { throw 'Signed executable failed publisher verification.' }
    }
    & (Join-Path $PSScriptRoot 'write-inventory.ps1') -Root $package
    # Signed bytes must be tested on the architecture that will run them.
    foreach ($path in @((Join-Path $package 'SurfaceInsight.exe'), $SingleFile)) {
        $process = Start-Process $path -ArgumentList '--self-test','--ci' -PassThru
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Signed release self-test timed out.' }
        if ($process.ExitCode -ne 0) { throw "Signed self-test failed: $path" }
    }
    Remove-Item -LiteralPath $Zip
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $Zip -CompressionLevel Optimal
} finally {
    if ($cert) { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -DeleteKey -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $root -Recurse -Force
}
