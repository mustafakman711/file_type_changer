<#
Run ONCE, elevated (Start-Process powershell -Verb RunAs ...). It:
  1. trusts the public Nashi signing certificate for packages by importing
     NashiLocalSigning.cer into Cert:\LocalMachine\TrustedPeople (this is the
     only step that needs administrator rights), and
  2. registers the signed sparse package for the signed-in user with the Nashi
     install folder as its external location, which adds "Convert with Nashi" /
     "Nashi tools..." to the Windows 11 top-level Explorer menu.
A transcript is written next to this script (install-explorer-menu.log).
build-sparse-package.ps1 fills in the install folder below.
#>
param([string]$InstallDirectory = '__INSTALL_DIRECTORY__')
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'install-explorer-menu.log'; Start-Transcript -Path $log -Force | Out-Null
try {
  $cer = Join-Path $PSScriptRoot 'NashiLocalSigning.cer'; $msix = Join-Path $PSScriptRoot 'Nashi.ExplorerMenu.msix'
  foreach ($required in $cer, $msix, (Join-Path $InstallDirectory 'Nashi.exe'), (Join-Path $InstallDirectory 'NashiShell.exe')) {
    if (-not (Test-Path $required)) { throw "Missing $required. Deploy the new Nashi build to $InstallDirectory first." }
  }
  $public = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer
  if ($public.HasPrivateKey) { throw 'Refusing to import a certificate file that contains a private key.' }
  if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $public.Thumbprint)) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  }
  Write-Host "Trusted $($public.Subject) ($($public.Thumbprint)) in LocalMachine\TrustedPeople"
  Get-AppxPackage -Name Nashi.ExplorerMenu | Remove-AppxPackage
  Add-AppxPackage -Path $msix -ExternalLocation $InstallDirectory
  $package = Get-AppxPackage -Name Nashi.ExplorerMenu
  if (-not $package) { throw 'The package did not register.' }
  Write-Host "Registered $($package.PackageFullName) -> $InstallDirectory"
  Write-Host 'Done. Right-click a file in Explorer. If the entry is missing, restart Explorer or sign out and back in.'
} catch {
  Write-Host "FAILED: $($_.Exception.Message)"; throw
} finally { Stop-Transcript | Out-Null }
