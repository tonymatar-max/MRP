<#
.SYNOPSIS
  Install (or reinstall) Cobalt MRP as a Windows service. Run from an ELEVATED PowerShell.

.PARAMETER Dest
  The published folder produced by publish.ps1. Default: C:\CobaltMRP

.PARAMETER Name
  Windows service name. Default: CobaltMRP

.PARAMETER Account
  Service log-on account. Default: LocalSystem. For a domain/local account pass e.g. ".\svc_mrp" together
  with -Password. NOTE: with SQL Server "Windows" authentication the database sees the SERVICE ACCOUNT
  (LocalSystem shows up as DOMAIN\MACHINE$), so either use SQL authentication in Settings or run the
  service as an account that has been granted access to the databases.

.PARAMETER Password
  Password for -Account (omit for LocalSystem / a gMSA).

.EXAMPLE
  ./deploy/install-service.ps1
  ./deploy/install-service.ps1 -Account "CORP\svc_mrp" -Password "..."
#>
param(
  [string]$Dest    = "C:\CobaltMRP",
  [string]$Name    = "CobaltMRP",
  [string]$Display = "Cobalt MRP",
  [string]$Account = "LocalSystem",
  [string]$Password
)
$ErrorActionPreference = "Stop"

# Must be elevated: creating a service and an event-log source both need admin.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$exe = Join-Path $Dest "CobaltMrp.Api.exe"
if (-not (Test-Path $exe)) { throw "$exe not found. Run deploy\publish.ps1 -Dest '$Dest' first." }

# Event-log source used by Program.cs (builder.Logging.AddEventLog). Create it as admin so the service
# account (which may not be able to create it) can write to it.
if (-not [System.Diagnostics.EventLog]::SourceExists($Display)) {
  New-EventLog -LogName Application -Source $Display
  Write-Host "Created event-log source '$Display'." -ForegroundColor DarkGray
}

# Remove an existing service so this script is a clean reinstall. The data\ folder is left alone.
$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing) {
  Write-Host "Stopping and removing existing service '$Name'..." -ForegroundColor Yellow
  if ($existing.Status -ne 'Stopped') { Stop-Service -Name $Name -Force }
  sc.exe delete $Name | Out-Null
  Start-Sleep -Seconds 2
}

# The service account must be able to write the data folder (settings, users, keys).
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "data") | Out-Null

Write-Host "Creating service '$Name'..." -ForegroundColor Cyan
$params = @{
  Name           = $Name
  BinaryPathName = "`"$exe`""
  DisplayName    = $Display
  Description    = "Cobalt MRP planning engine, API and web UI (http://localhost:5050)."
  StartupType    = "Automatic"
}
if ($Account -ne "LocalSystem") {
  if (-not $Password) { throw "Provide -Password for account '$Account' (or use the default LocalSystem)." }
  $sec = ConvertTo-SecureString $Password -AsPlainText -Force
  $params.Credential = New-Object System.Management.Automation.PSCredential($Account, $sec)
  icacls (Join-Path $Dest "data") /grant "${Account}:(OI)(CI)M" | Out-Null
}
New-Service @params | Out-Null

# Restart automatically if it ever crashes.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $Name
Start-Sleep -Seconds 3
Get-Service -Name $Name | Format-Table -AutoSize

Write-Host "`nInstalled. Open http://localhost:5050 on this server to create the first administrator," -ForegroundColor Green
Write-Host "then open Settings to configure the databases and the Service Layer." -ForegroundColor Green
