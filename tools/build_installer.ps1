# Builds GBrowser and the single-file installer out\GBrowserSetup-<version>.exe (also used by GitHub Actions).
# Needs the compiled filters in filters\ (tools/build_filters.py). Usage: pwsh tools/build_installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$ver = ([regex]::Match((Get-Content GBrowser.csproj -Raw), '<Version>(.*?)</Version>')).Groups[1].Value
$app = Join-Path $root 'out\GBrowser'
$zip = Join-Path $root 'out\payload.zip'

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet build GBrowser.csproj -c Release -nodeReuse:false -maxcpucount:1 -p:UseSharedCompilation=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'GBrowser build failed' }
Remove-Item (Join-Path $app 'Microsoft.Web.WebView2.Wpf.dll') -ErrorAction SilentlyContinue
Get-ChildItem $app -Filter *.pdb | Remove-Item
foreach ($f in 'GBrowser.exe', 'pages\newtab.html', 'assets\gbab.js', 'filters\hosts.bin', 'filters\net.txt', 'filters\cosm.txt') {
  if (-not (Test-Path (Join-Path $app $f))) { throw "missing $f" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip }
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$setupOut = Join-Path $root 'out\setup'
dotnet build installer\GBrowserSetup.csproj -c Release -nodeReuse:false -maxcpucount:1 -p:UseSharedCompilation=false "-p:PayloadZip=$zip" -o $setupOut
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed' }
$exe = Join-Path $root "out\GBrowserSetup-$ver.exe"
Copy-Item (Join-Path $setupOut 'GBrowserSetup.exe') $exe -Force
$h = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"version=$ver"
"installer=$exe"
"size=$([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB"
"sha256=$h"
