# Builds dist\SrvTV-Setup.exe: one file that installs Srv TV on any 64-bit
# Windows 10/11 PC (no .NET or admin needed on that PC).
#   powershell -ExecutionPolicy Bypass -File installer\build-setup.ps1
param(
    [string]$Dotnet = "dotnet"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repo "publish\setup-work"
$app = Join-Path $work "app"
$dist = Join-Path $repo "dist"
$zip = Join-Path $work "payload.zip"
$out = Join-Path $dist "SrvTV-Setup.exe"

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

Write-Host "1/4 Publishing the app (self-contained, win-x64)..."
& $Dotnet publish (Join-Path $repo "src\SrvTv\SrvTv.csproj") -c Release -r win-x64 --self-contained true `
    -o (Join-Path $work "full") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Host "2/4 Trimming files a 64-bit PC never uses..."
# Only the 64-bit video engine is needed; drop the x86/arm64 copies and symbols.
robocopy (Join-Path $work "full") $app /E /XD win-x86 win-arm64 /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed" }
if (-not (Test-Path (Join-Path $app "libvlc\win-x64\libvlc.dll"))) { throw "libvlc win-x64 missing" }

Write-Host "3/4 Compressing..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "4/4 Building the setup and attaching the app..."
& $Dotnet build (Join-Path $PSScriptRoot "SrvTvSetup\SrvTvSetup.csproj") -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }
$stub = Join-Path $PSScriptRoot "SrvTvSetup\bin\Release\net48\SrvTvSetup.exe"

$zipLen = (Get-Item $zip).Length
$fs = [IO.File]::Create($out)
try {
    foreach ($part in @($stub, $zip)) {
        $in = [IO.File]::OpenRead($part)
        try { $in.CopyTo($fs) } finally { $in.Dispose() }
    }
    $trailer = [BitConverter]::GetBytes([long]$zipLen) + [Text.Encoding]::ASCII.GetBytes("SRVTVPK1")
    $fs.Write($trailer, 0, $trailer.Length)
} finally { $fs.Dispose() }

Remove-Item (Join-Path $work "full") -Recurse -Force
"{0}  ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB)
