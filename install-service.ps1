<#
.SYNOPSIS
  Installs, updates, or removes the PortProSageSync Windows Service.

.EXAMPLE
  # Publish first:
  dotnet publish PortProSage.Service -c Release -o C:\PortProSageSync\bin --self-contained false

  # Then install:
  .\install-service.ps1 -Action Install -BinPath "C:\PortProSageSync\bin\PortProSage.Service.exe"

.EXAMPLE
  .\install-service.ps1 -Action Uninstall
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall", "Start", "Stop")]
    [string]$Action,

    [string]$ServiceName = "PortProSageSync",
    [string]$BinPath,
    [string]$DisplayName = "PortPro to Sage 50 Invoice Sync",

    [ValidateSet("Development", "Production")]
    [string]$Environment = "Production"
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

switch ($Action) {
    "Install" {
        if (-not $BinPath) { throw "-BinPath is required for Install (path to PortProSage.Service.exe)." }
        New-Service -Name $ServiceName -BinaryPathName $BinPath -DisplayName $DisplayName -StartupType Automatic

        # Windows services do NOT inherit the logged-in user's environment variables,
        # so DOTNET_ENVIRONMENT must be set on the service itself (as opposed to via
        # setx/System Properties) for appsettings.{Environment}.json to be picked up.
        $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
        Set-ItemProperty -Path $regPath -Name "Environment" -Value @("DOTNET_ENVIRONMENT=$Environment")

        Write-Host "Service '$ServiceName' installed with DOTNET_ENVIRONMENT=$Environment."
        Write-Host "Start it with: .\install-service.ps1 -Action Start"
    }
    "Uninstall" {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName
        Write-Host "Service '$ServiceName' removed."
    }
    "Start" {
        Start-Service -Name $ServiceName
        Write-Host "Service '$ServiceName' started."
    }
    "Stop" {
        Stop-Service -Name $ServiceName
        Write-Host "Service '$ServiceName' stopped."
    }
}
