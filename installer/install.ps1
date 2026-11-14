# Installs the latest Srv TV on this PC. Run in PowerShell:
#   irm https://github.com/srv883/SrvTV-Windows/releases/latest/download/install.ps1 | iex
# Downloading through PowerShell (instead of a browser) means Windows
# doesn't mark the file as "from the internet", so no SmartScreen prompt.
$ErrorActionPreference = "Stop"
$base = "https://github.com/srv883/SrvTV-Windows/releases/latest/download"
$dir = Join-Path $env:TEMP "SrvTvInstall"
New-Item -ItemType Directory -Force $dir | Out-Null
$setup = Join-Path $dir "SrvTV-Setup.exe"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Write-Host "Downloading Srv TV..."
$ProgressPreference = "SilentlyContinue"
Invoke-WebRequest "$base/SrvTV-Setup.exe" -OutFile $setup -UseBasicParsing
$expected = ((Invoke-WebRequest "$base/SrvTV-Setup.exe.sha256" -UseBasicParsing).Content -split '\s+')[0].Trim().ToLower()
$actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { Remove-Item $setup; throw "Download check failed. Try again." }
Write-Host "Installing..."
Start-Process $setup -Wait
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
