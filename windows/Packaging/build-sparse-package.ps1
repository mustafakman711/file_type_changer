<#
Builds and signs the Nashi sparse package (Windows 11 top-level Explorer menu).
Runs WITHOUT administrator rights:

  1. Finds makeappx/signtool (Windows SDK) or downloads the pinned, Microsoft-
     signed Microsoft.Windows.SDK.BuildTools NuGet package (SHA-256 checked).
  2. Reuses or creates the self-signed code-signing certificate
     "CN=Nashi Local Signing" in Cert:\CurrentUser\My. Its private key is
     created non-exportable and never leaves this user profile; only the
     public .cer is written to the output folder.
  3. Packs AppxManifest.xml plus generated logos into Nashi.ExplorerMenu.msix
     (makeappx /nv: the executables live in the external install folder) and
     signs it with SHA-256.
  4. Writes install-explorer-menu.ps1 / uninstall-explorer-menu.ps1 next to it
     with the install folder filled in. Only those two need elevation.
#>
param(
  [Parameter(Mandatory)][string]$InstallDirectory,
  [string]$OutDirectory = (Join-Path $PSScriptRoot '../../build/explorer-menu'),
  [string]$BuildTools,
  [string]$Version
)
$ErrorActionPreference = 'Stop'
$Subject = 'CN=Nashi Local Signing'
$OutDirectory = [IO.Path]::GetFullPath($OutDirectory); $InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
New-Item -ItemType Directory -Force $OutDirectory | Out-Null
if (-not $Version) {
  $props = [xml](Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)
  $Version = (@($props.Project.PropertyGroup.Version) -ne $null)[0] + '.0'
}

function Find-Tool([string]$name) {
  if ($BuildTools) { $candidate = Get-ChildItem $BuildTools -Recurse -Filter $name | Where-Object FullName -match '\\x64\\' | Select-Object -First 1; if ($candidate) { return $candidate.FullName } }
  $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  if (Test-Path $kits) { $candidate = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1; if ($candidate) { return $candidate.FullName } }
  return $null
}
$makeappx = Find-Tool 'makeappx.exe'; $signtool = Find-Tool 'signtool.exe'
if (-not $makeappx -or -not $signtool) {
  $toolsVersion = '10.0.28000.1839'; $expected = 'B9A0299EAC4CD2495745971939C989F3197A39B58F228DAFA7A61E5928BBCBDF'
  $cache = Join-Path $env:LOCALAPPDATA "Nashi\BuildTools\$toolsVersion"; $package = Join-Path $cache 'buildtools.nupkg'
  if (-not (Test-Path (Join-Path $cache 'x'))) {
    New-Item -ItemType Directory -Force $cache | Out-Null
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$toolsVersion/microsoft.windows.sdk.buildtools.$toolsVersion.nupkg" -OutFile $package
    if ((Get-FileHash $package -Algorithm SHA256).Hash -ne $expected) { Remove-Item $package; throw 'Windows SDK BuildTools download failed its SHA-256 check.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::ExtractToDirectory($package, (Join-Path $cache 'x'))
  }
  $BuildTools = Join-Path $cache 'x'; $makeappx = Find-Tool 'makeappx.exe'; $signtool = Find-Tool 'signtool.exe'
}
foreach ($tool in $makeappx, $signtool) { if ((Get-AuthenticodeSignature $tool).Status -ne 'Valid') { throw "$tool is not validly signed." } }

# Signing certificate: CurrentUser\My only, private key non-exportable.
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
  $certificate = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'Nashi local package signing' -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  Write-Host "Created signing certificate $($certificate.Thumbprint) in Cert:\CurrentUser\My"
}
$cer = Join-Path $OutDirectory 'NashiLocalSigning.cer'
Export-Certificate -Cert $certificate -FilePath $cer -Type CERT | Out-Null

# Layout: manifest + logos only.
$layout = Join-Path $OutDirectory 'layout'; Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).Replace('$VERSION$', $Version).Replace('$PUBLISHER$', $Subject)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Image]::FromFile((Resolve-Path (Join-Path $PSScriptRoot '../../Resources/Brand/NashiIcon.png')))
try {
  foreach ($logo in @(@('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150), @('StoreLogo.png', 50))) {
    $bitmap = New-Object Drawing.Bitmap $logo[1], $logo[1]; $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $graphics.DrawImage($icon, 0, 0, $logo[1], $logo[1]); $graphics.Dispose()
    $bitmap.Save((Join-Path $layout "Assets\$($logo[0])"), [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
  }
} finally { $icon.Dispose() }

$msix = Join-Path $OutDirectory 'Nashi.ExplorerMenu.msix'; Remove-Item $msix -ErrorAction SilentlyContinue
& $makeappx pack /d $layout /p $msix /nv /o | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
& $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $msix | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }

foreach ($script in 'install-explorer-menu.ps1', 'uninstall-explorer-menu.ps1') {
  $text = (Get-Content (Join-Path $PSScriptRoot $script) -Raw).Replace('__INSTALL_DIRECTORY__', $InstallDirectory).Replace('__THUMBPRINT__', $certificate.Thumbprint)
  [IO.File]::WriteAllText((Join-Path $OutDirectory $script), $text, (New-Object Text.UTF8Encoding $true))
}
Write-Host "Package:     $msix"
Write-Host "Certificate: $cer (public key only; thumbprint $($certificate.Thumbprint))"
Write-Host "Install (elevated, once): $(Join-Path $OutDirectory 'install-explorer-menu.ps1')"
