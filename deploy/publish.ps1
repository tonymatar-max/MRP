<#
.SYNOPSIS
  Build the UI and publish the API to a folder ready to run as a Windows service.

.DESCRIPTION
  1. Builds the React client (into src/CobaltMrp.Api/wwwroot).
  2. Publishes the .NET API for win-x64 into the target folder.
  The published folder carries appsettings.json (which binds http://localhost:5050) and the built UI.
  An existing data\ folder in the target (settings, users, keys) is never touched, so this is also the
  upgrade path: publish over the top, then restart the service.

.PARAMETER Dest
  Where to publish. Default: C:\CobaltMRP

.EXAMPLE
  ./deploy/publish.ps1 -Dest C:\CobaltMRP
#>
param(
  [string]$Dest = "C:\CobaltMRP"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (parent of deploy\)

Write-Host "Building UI..." -ForegroundColor Cyan
Push-Location (Join-Path $root "web")
try {
  # Native commands go through cmd /c: Windows PowerShell 5.1 turns any stderr output (npm prints harmless
  # warnings there) into a terminating error under $ErrorActionPreference = "Stop".
  cmd /c "npm install 2>&1"
  if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
  cmd /c "npm run build 2>&1"
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
} finally { Pop-Location }

Write-Host "Publishing API to $Dest ..." -ForegroundColor Cyan
Push-Location (Join-Path $root "src\CobaltMrp.Api")
try {
  cmd /c "dotnet publish -c Release -r win-x64 --self-contained false -o `"$Dest`" 2>&1"
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
} finally { Pop-Location }

Write-Host "`nPublished to $Dest" -ForegroundColor Green
if (Get-Service -Name CobaltMRP -ErrorAction SilentlyContinue) {
  Write-Host "Upgrade: restart the service to pick up the new files:  Restart-Service CobaltMRP" -ForegroundColor Yellow
} else {
  Write-Host "Next: run deploy\install-service.ps1 from an elevated PowerShell." -ForegroundColor Yellow
}
