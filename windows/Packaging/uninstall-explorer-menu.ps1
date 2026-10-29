<#
Removes the Windows 11 Explorer menu package and its certificate trust.
Run elevated (removing the certificate from LocalMachine\TrustedPeople needs
administrator rights; the package removal itself does not).
  -RemoveSigningKey   also deletes the private signing certificate from
                      Cert:\CurrentUser\My (rebuilding the package then creates
                      a new one, which must be trusted again).
  -RemoveClassicMenu  also deletes the per-user "Show more options" entries
                      (Nashi recreates them at start unless Settings > "Show
                      Nashi in Explorer's right-click menu" is off).
#>
param([switch]$RemoveSigningKey, [switch]$RemoveClassicMenu, [string]$Thumbprint = '__THUMBPRINT__')
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'uninstall-explorer-menu.log'; Start-Transcript -Path $log -Force | Out-Null
try {
  Get-AppxPackage -Name Nashi.ExplorerMenu | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName; Write-Host "Removed $($_.PackageFullName)" }
  $trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq 'CN=Nashi Local Signing' -and ($Thumbprint -like '__*' -or $_.Thumbprint -eq $Thumbprint) })
  foreach ($certificate in $trusted) { Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"; Write-Host "Untrusted $($certificate.Thumbprint)" }
  if ($RemoveSigningKey) {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=Nashi Local Signing' | ForEach-Object { Remove-Item -Path "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey; Write-Host "Deleted signing key $($_.Thumbprint)" }
  }
  if ($RemoveClassicMenu) {
    $classes = 'HKCU:\Software\Classes'
    Get-ChildItem "$classes\SystemFileAssociations" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item "$($_.PSPath)\shell\Nashi.Convert" -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($type in '*', 'Directory') { Remove-Item -LiteralPath "$classes\$type\shell\Nashi.Tools" -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-ItemProperty -Path 'HKCU:\Software\Nashi' -Name ExplorerMenu -ErrorAction SilentlyContinue
    Write-Host 'Removed classic Explorer entries (turn the Settings switch off to keep them away).'
  }
} finally { Stop-Transcript | Out-Null }
