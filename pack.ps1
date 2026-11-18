param(
    [switch]$Install,
    [switch]$SkipBridges,
    [string]$Configuration = "Release",
    [string]$TuneLabSdkRoot = $env:TUNELAB_SDK_ROOT,
    [string]$Dotnet = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($TuneLabSdkRoot)) {
    $TuneLabSdkRoot = Join-Path $root "../../TuneLab2"
}

if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $Dotnet = $command.Source
    }
    elseif (Test-Path "Y:\devtools\dotnet\dotnet.exe") {
        $Dotnet = "Y:\devtools\dotnet\dotnet.exe"
    }
    else {
        throw "dotnet was not found. Pass -Dotnet <path>."
    }
}

$project = Join-Path $root "TuneLab.VstInstruments.csproj"
$outDir = Join-Path $root "bin/$Configuration"
$distDir = Join-Path $root "dist"
$packageName = "VSTinstriumentForTunelab"
$stage = Join-Path $distDir $packageName
$bridgeRoot = Join-Path $root "artifacts/bridge"

Write-Host "Building the extension ($Configuration)..."
& $Dotnet build $project -c $Configuration "-p:TuneLabSdkRoot=$TuneLabSdkRoot"
if ($LASTEXITCODE -ne 0) { throw "the extension build failed" }

if (-not $SkipBridges) {
    Write-Host "Publishing helper processes..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "publish-bridges.ps1") `
        -Configuration $Configuration `
        -Dotnet $Dotnet
    if ($LASTEXITCODE -ne 0) { throw "publishing the helper processes failed" }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $outDir "manifest.json") $stage
Copy-Item (Join-Path $outDir "Introduction.md") $stage
Copy-Item (Join-Path $outDir "THIRD-PARTY-NOTICES.txt") $stage

Get-ChildItem -Path $outDir -Filter *.dll |
    Where-Object { $_.Name -notin @("TuneLab.SDK.dll", "TuneLab.Foundation.dll") } |
    Copy-Item -Destination $stage

if (Test-Path $bridgeRoot) {
    Copy-Item $bridgeRoot (Join-Path $stage "bridge") -Recurse
}
else {
    Write-Warning "no bridge folder found; the package will not be able to host plugins"
}

$tlx = Join-Path $distDir "$packageName.tlx"
if (Test-Path $tlx) { Remove-Item $tlx -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $tlx,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "Package: $tlx"

if ($Install) {
    $target = Join-Path $env:APPDATA "TuneLab/Extensions/com.tunelab.vstinstruments"

    if (Get-Process TuneLab -ErrorAction SilentlyContinue) {
        throw "TuneLab is running. Close it before installing (it locks extension DLLs)."
    }

    # Extract to a staging folder, then copy over the existing extension folder.
    # Existing files that are not part of the package are preserved.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "tunelab-vstinstruments-install"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    [System.IO.Compression.ZipFile]::ExtractToDirectory($tlx, $staging)

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $staging "*") $target -Recurse -Force
    Remove-Item $staging -Recurse -Force

    Write-Host "Installed to: $target"
    Write-Host "Catalog cache: $env:LOCALAPPDATA\TuneLab\VstInstruments"
}