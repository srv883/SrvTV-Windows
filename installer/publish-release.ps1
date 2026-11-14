# Pushes an update to every installed Srv TV.
#   powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1 -Version 1.2.0 -Notes "What changed"
# Bumps the version, builds dist\SrvTV-Setup.exe, tags vX.Y.Z and creates a
# GitHub release with the setup, its SHA-256 and install.ps1. Installed
# copies find it within a few hours (or via Settings > Check for updates),
# download it in the background and install when the app is closed.
# Needs: GitHub CLI signed in (gh auth login) and the repo public, so
# installed copies can read releases without a password.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "Bug fixes and improvements.",
    [string]$Dotnet = "dotnet"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.0" }

$props = Join-Path $repo "Directory.Build.props"
$text = Get-Content $props -Raw
$current = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value
if ([version]$Version -le [version]$current) { throw "New version $Version must be higher than $current" }
$text = $text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($props, $text)

& (Join-Path $PSScriptRoot "build-setup.ps1") -Dotnet $Dotnet
$setup = Join-Path $repo "dist\SrvTV-Setup.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
$hashFile = "$setup.sha256"
[IO.File]::WriteAllText($hashFile, "$hash  SrvTV-Setup.exe`n")

git add Directory.Build.props
git commit -m "Release $Version" | Out-Null
git tag "v$Version"
git push origin HEAD "v$Version"
if ($LASTEXITCODE -ne 0) { throw "git push failed (is github.com reachable?)" }

gh release create "v$Version" $setup $hashFile (Join-Path $PSScriptRoot "install.ps1") `
    --repo srv883/SrvTV-Windows --title "Srv TV $Version" --notes $Notes --latest
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Released $Version. Installed copies will pick it up automatically."
