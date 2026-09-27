<#
  Build-Package.ps1 — publish, package as MSIX, sign.

  Run from anywhere:  pwsh -File packaging\Build-Package.ps1

  Signing:
    - Pass -SignThumbprint <thumbprint> (or set $env:SIGN_THUMBPRINT) to sign with a
      certificate already in Cert:\CurrentUser\My, e.g. a real CA cert.
    - Leave it empty to fall back to a generated self-signed certificate whose subject is
      $Subject (see the -Subject parameter below).

  Versioning (single source of truth):
    The version lives in <Version> in AutoClicker.csproj. This script reads it, injects
    "major.minor.patch.0" into packaging\AppxManifest.xml, and packs the patched manifest,
    so the MSIX version can never drift from the assembly version.
#>
[CmdletBinding()]
param(
    # Thumbprint of a code-signing certificate in Cert:\CurrentUser\My. Empty = self-signed
    # fallback. Use YOUR OWN thumbprint (or the env var) — do not commit it:
    #   -SignThumbprint 'BDD3B36174D46CEE989D8DF9FFB6287A0ACE9A82'   # example only
    [string]$SignThumbprint = $env:SIGN_THUMBPRINT,

    # Subject of the self-signed fallback certificate. MUST match the Publisher attribute in
    # packaging\AppxManifest.xml (this script fails fast when they differ).
    [string]$Subject = 'CN=Peter Eg, O=PETER EG, L=Singapore, C=SG',

    # When set, pack the MSIX but skip signing entirely (e.g. CI, which has no cert).
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

# dotnet is not on PATH everywhere (e.g. installed per-user); resolve it explicitly.
function Resolve-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }
    throw "dotnet was not found on PATH nor at '$local'. Install the .NET 10 SDK."
}
$dotnet = Resolve-DotNet

$proj      = Join-Path $PSScriptRoot '..\AutoClicker.csproj' | Resolve-Path
$manifest  = Join-Path $PSScriptRoot 'AppxManifest.xml' | Resolve-Path
$root      = Split-Path $proj
$pkgDir    = $PSScriptRoot
$outDir    = Join-Path $root 'installer'
$staging   = Join-Path $outDir 'staging'
$assets    = Join-Path $staging 'Assets'
$srcAssets = Join-Path $pkgDir 'Assets'
$msix      = Join-Path $outDir 'AutoClicker.msix'
$pfx       = Join-Path $outDir 'AutoClicker-codesign.pfx'
$cer       = Join-Path $outDir 'AutoClicker-codesign.cer'

# --- single-source the version from the csproj ---
$projXml = [xml](Get-Content $proj -Raw)
$versionNode = $projXml.GetElementsByTagName('Version') | Select-Object -First 1
if (-not $versionNode) { throw "Could not find a <Version> element in '$proj'." }
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$version' in '$proj' is not a 3-part version (major.minor.patch)."
}
$fourPart = "$version.0"

# Read/write the manifest as UTF-8 (no BOM) so the em dash in the Description survives.
$utf8 = [System.Text.UTF8Encoding]::new($false)
$manifestText = [System.IO.File]::ReadAllText($manifest, $utf8)

Write-Host "==> Inject version $fourPart into AppxManifest.xml"
$patched = [regex]::Replace($manifestText, 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$fourPart`"", 1)
if ($patched -eq $manifestText) {
    throw "Could not find a Version attribute in '$manifest' to patch."
}

$publisherMatch = [regex]::Match($manifestText, 'Publisher="([^"]+)"')
if (-not $publisherMatch.Success) {
    throw "Could not find a Publisher attribute in '$manifest'."
}
$publisher = $publisherMatch.Groups[1].Value
Write-Host "==> Manifest publisher: $publisher"

# --- locate Windows SDK tools (fail fast if missing) ---
$sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
if (-not (Test-Path $sdkBin)) {
    throw "Windows SDK not found at '$sdkBin'. Install the Windows 10/11 SDK (makeappx + signtool)."
}
$makeappx = Get-ChildItem $sdkBin -Recurse -Filter makeappx.exe | ? FullName -match 'x64' | sort FullName -desc | select -first 1 -exp FullName
$signtool = Get-ChildItem $sdkBin -Recurse -Filter signtool.exe | ? FullName -match 'x64' | sort FullName -desc | select -first 1 -exp FullName
if (-not $makeappx) { throw "makeappx.exe not found under '$sdkBin'." }
if (-not $signtool) { throw "signtool.exe not found under '$sdkBin'." }

Write-Host "==> Clean staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Write-Host "==> Publish self-contained win-x64"
& $dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
    -o $staging | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "==> Copy logo assets + manifest (with injected version)"
Copy-Item (Join-Path $srcAssets '*.png') $assets -Force
[System.IO.File]::WriteAllText((Join-Path $staging 'AppxManifest.xml'), $patched, $utf8)

Write-Host "==> Pack MSIX"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $staging /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed." }

if ($SkipSign) {
    Write-Host "==> Skipping signing (unsigned MSIX)"
}
elseif ($SignThumbprint) {
    $cert = Get-Item "Cert:\CurrentUser\My\$SignThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Signing cert $SignThumbprint not found in CurrentUser\My" }
    if ($cert.Subject -ne $publisher) {
        throw "Certificate subject '$($cert.Subject)' does not match AppxManifest.xml Publisher '$publisher'."
    }
    Write-Host "==> Sign MSIX with store cert: $($cert.Subject)"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null   # public cert, for reference
    & $signtool sign /fd SHA256 /sha1 $SignThumbprint `
        /tr http://timestamp.digicert.com /td SHA256 $msix
}
else {
    Write-Host "==> Ensure self-signed code-signing cert ($Subject)"
    $cert = Get-ChildItem Cert:\CurrentUser\My | ? { $_.Subject -eq $Subject -and $_.EnhancedKeyUsageList.FriendlyName -contains 'Code Signing' } | select -first 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
            -KeyUsage DigitalSignature -FriendlyName 'Auto Clicker code signing' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
            -NotAfter (Get-Date).AddYears(3)
    }
    if ($cert.Subject -ne $publisher) {
        throw "Self-signed cert subject '$($cert.Subject)' does not match AppxManifest.xml Publisher '$publisher'."
    }
    $pfxPass = [Guid]::NewGuid().ToString()  # ephemeral; only used by the self-signed fallback
    $sec = ConvertTo-SecureString $pfxPass -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec | Out-Null
    Export-Certificate  -Cert $cert -FilePath $cer | Out-Null
    Write-Host "==> Sign MSIX with self-signed key"
    & $signtool sign /fd SHA256 /a /f $pfx /p $pfxPass `
        /tr http://timestamp.digicert.com /td SHA256 $msix
}

if ($SkipSign) {
    Write-Host ""
    Write-Host "DONE (unsigned): MSIX = $msix"
}
else {
    Write-Host ""
    Write-Host "DONE: MSIX = $msix"
    & $signtool verify /pa $msix 2>&1 | Out-Host
}
