<#
  Build-Package.ps1 — publish, package as MSIX, sign with a self-signed code cert.
  Run from anywhere:  pwsh -File packaging\Build-Package.ps1
#>
$ErrorActionPreference = 'Stop'

$proj      = Join-Path $PSScriptRoot '..\AutoClicker.csproj' | Resolve-Path
$root      = Split-Path $proj
$pkgDir    = $PSScriptRoot
$outDir    = Join-Path $root 'installer'
$staging   = Join-Path $outDir 'staging'
$assets    = Join-Path $staging 'Assets'
$srcAssets = Join-Path $pkgDir 'Assets'
$msix      = Join-Path $outDir 'AutoClicker.msix'
$pfx       = Join-Path $outDir 'AutoClicker-codesign.pfx'
$cer       = Join-Path $outDir 'AutoClicker-codesign.cer'

# Signing: if $signThumbprint is set, sign with that store cert (e.g. a real CA cert).
# Otherwise fall back to generating/using a self-signed cert with subject $subject.
$signThumbprint = 'BDD3B36174D46CEE989D8DF9FFB6287A0ACE9A82'  # Certum CN=Peter Eg
$subject   = 'CN=Peter Eg, O=PETER EG, L=Singapore, C=SG'     # MUST match Publisher in AppxManifest.xml
$pfxPass   = [Guid]::NewGuid().ToString()  # ephemeral; only used by the self-signed fallback

# --- locate Windows SDK tools ---
$sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
$makeappx = Get-ChildItem $sdkBin -Recurse -Filter makeappx.exe | ? FullName -match 'x64' | sort FullName -desc | select -first 1 -exp FullName
$signtool = Get-ChildItem $sdkBin -Recurse -Filter signtool.exe | ? FullName -match 'x64' | sort FullName -desc | select -first 1 -exp FullName

Write-Host "==> Clean staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Write-Host "==> Publish self-contained win-x64"
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
    -o $staging | Out-Null

Write-Host "==> Copy logo assets + manifest"
Copy-Item (Join-Path $srcAssets '*.png') $assets -Force
Copy-Item (Join-Path $pkgDir 'AppxManifest.xml') (Join-Path $staging 'AppxManifest.xml') -Force

Write-Host "==> Pack MSIX"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $staging /p $msix /o | Out-Null

if ($signThumbprint) {
    $cert = Get-Item "Cert:\CurrentUser\My\$signThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Signing cert $signThumbprint not found in CurrentUser\My" }
    Write-Host "==> Sign MSIX with store cert: $($cert.Subject)"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null   # public cert, for reference
    & $signtool sign /fd SHA256 /sha1 $signThumbprint `
        /tr http://timestamp.digicert.com /td SHA256 $msix
}
else {
    Write-Host "==> Ensure self-signed code-signing cert ($subject)"
    $cert = Get-ChildItem Cert:\CurrentUser\My | ? { $_.Subject -eq $subject -and $_.EnhancedKeyUsageList.FriendlyName -contains 'Code Signing' } | select -first 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -KeyUsage DigitalSignature -FriendlyName 'Auto Clicker code signing' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
            -NotAfter (Get-Date).AddYears(3)
    }
    $sec = ConvertTo-SecureString $pfxPass -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec | Out-Null
    Export-Certificate  -Cert $cert -FilePath $cer | Out-Null
    Write-Host "==> Sign MSIX with self-signed key"
    & $signtool sign /fd SHA256 /a /f $pfx /p $pfxPass `
        /tr http://timestamp.digicert.com /td SHA256 $msix
}

Write-Host ""
Write-Host "DONE: MSIX = $msix"
& $signtool verify /pa $msix 2>&1 | Out-Host
