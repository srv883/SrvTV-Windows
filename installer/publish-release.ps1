# Publishes ONE vX.Y.Z release for every Srv TV app: Android TV, Windows and web.
#   powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1 -Version 1.3.0 -Notes "What changed"
#
# Bumps the version in all three source trees, builds the Android APKs, the
# Windows installer and SrvTV-web.zip, then uploads them all to a single
# GitHub release tagged vX.Y.Z on srv883/SrvTV-Windows. All three apps read
# that same tag and release notes and download only their own asset:
#   SrvTV-sourav.apk / SrvTV-parents.apk  (Android TV)
#   SrvTV-Setup.exe + install.ps1         (Windows)
#   SrvTV-web.zip                         (browser app on the phone)
# Every asset has a sibling .sha256; each updater verifies it before use.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "Bug fixes and improvements.",
    [string]$AndroidRepo = "",
    [string]$Dotnet = "dotnet"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $AndroidRepo) { $AndroidRepo = Join-Path (Split-Path -Parent $repo) "IPTVApp" }
$webDir = Join-Path $AndroidRepo "web"
if (-not (Test-Path $AndroidRepo)) { throw "Android repo not found at $AndroidRepo (pass -AndroidRepo)" }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.3.0" }

function Get-Version([string]$path, [string]$pattern) {
    $text = [IO.File]::ReadAllText($path)
    $m = [regex]::Match($text, $pattern)
    if (-not $m.Success) { throw "Couldn't read version from $path" }
    return $m.Groups[1].Value
}

$androidVer = Get-Version (Join-Path $AndroidRepo "app\build.gradle.kts") 'findProperty\("srvVersion"\) as String\?\) \?: "([^"]+)"'
$windowsVer = Get-Version (Join-Path $repo "Directory.Build.props") '<Version>([^<]+)</Version>'
$webVerFile = Join-Path $webDir "version.json"
$webVer = (Get-Content $webVerFile -Raw | ConvertFrom-Json).version
$highest = @([version]$androidVer, [version]$windowsVer, [version]$webVer) | Sort-Object -Descending | Select-Object -First 1
if ([version]$Version -le $highest) { throw "New version $Version must be higher than $highest (Android $androidVer, Windows $windowsVer, web $webVer)" }

Write-Host "1/6 Building Android $Version..."
& (Join-Path $AndroidRepo "scripts\publish-android-release.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Android build failed" }

Write-Host "2/6 Building Windows $Version..."
$props = Join-Path $repo "Directory.Build.props"
$propsText = [IO.File]::ReadAllText($props) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($props, $propsText)
& (Join-Path $PSScriptRoot "build-setup.ps1") -Dotnet $Dotnet
if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
$setup = Join-Path $repo "dist\SrvTV-Setup.exe"
$setupHash = "$setup.sha256"
[IO.File]::WriteAllText($setupHash, "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())  SrvTV-Setup.exe`n")

Write-Host "3/6 Building SrvTV-web.zip..."
$webVersionJson = @{
    version = $Version
    notes = $Notes
} | ConvertTo-Json -Compress
[IO.File]::WriteAllText($webVerFile, $webVersionJson + "`n")
$webZip = Join-Path $repo "dist\SrvTV-web.zip"
if (Test-Path $webZip) { Remove-Item $webZip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($webZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @("server.py", "version.json", "static/index.html", "static/hls.min.js")) {
        $entry = $zip.CreateEntry($name)
        $stream = $entry.Open()
        try {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $webDir $name.Replace('/', '\')))
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
$webHash = "$webZip.sha256"
[IO.File]::WriteAllText($webHash, "$((Get-FileHash $webZip -Algorithm SHA256).Hash.ToLower())  SrvTV-web.zip`n")

Write-Host "4/6 Committing and tagging both source trees..."
git -C $AndroidRepo add app/build.gradle.kts web/version.json
git -C $AndroidRepo commit -m "Release $Version" | Out-Null
git -C $AndroidRepo tag "v$Version"
git add Directory.Build.props
git commit -m "Release $Version" | Out-Null
git tag "v$Version"

Write-Host "5/6 Pushing source trees..."
git -C $AndroidRepo push origin HEAD "v$Version"
if ($LASTEXITCODE -ne 0) { throw "Android push failed" }
git push origin HEAD "v$Version"
if ($LASTEXITCODE -ne 0) { throw "Windows push failed" }

Write-Host "6/6 Publishing the shared release..."
$assets = @()
foreach ($edition in "sourav", "parents") {
    $apk = Join-Path $AndroidRepo "dist\android\SrvTV-$edition.apk"
    $assets += $apk, "$apk.sha256"
}
$assets += $setup, $setupHash, (Join-Path $PSScriptRoot "install.ps1"), $webZip, $webHash
gh release create "v$Version" @assets --repo srv883/SrvTV-Windows `
    --title "Srv TV $Version" --notes $Notes --latest
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Released Srv TV $Version for Android TV, Windows and web."
