# Publish the helper process for each target runtime.
#
# The helper is a separate executable, not a ProjectReference: it must be
# self-contained (most machines do not install a global .NET runtime) and its
# runtime files must live in a per-RID folder (hostfxr/coreclr have the same
# name but different architectures and must never share a directory).
#
# Output layout expected by VstInstrumentBridgeClient:
#
#   <package>/bridge/win-x64/TuneLab.VstInstruments.Bridge.exe
#   <package>/bridge/win-x86/TuneLab.VstInstruments.Bridge.exe
#   <package>/bridge/linux-x64/TuneLab.VstInstruments.Bridge
#   ...
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\publish-bridges.ps1
#   powershell -ExecutionPolicy Bypass -File .\publish-bridges.ps1 -Runtime win-x64,win-x86
#
# Linux/macOS publishing works from Windows only when the matching host/runtime
# packs are available in the NuGet cache; otherwise the RID is skipped with a
# warning and should be published on that platform (or in CI).

param(
    [string]$Configuration = "Release",
    [string]$Dotnet = "",
    [string]$Runtime = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "Bridge/Process/TuneLab.VstInstruments.Bridge.csproj"
$outRoot = Join-Path $root "artifacts/bridge"

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

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $runtimeIds = @(
        "win-x64",
        "win-x86",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    )
}
else {
    $runtimeIds = $Runtime -split ',' |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

Write-Host "Publishing the bridge with $Dotnet"

foreach ($rid in $runtimeIds) {
    $target = Join-Path $outRoot $rid

    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null

    # MSBuild reuses the apphost from the previous RID unless the project's
    # intermediate output is cleared. That silently produced an x64 apphost in
    # the win-x86 package once; never again.
    Remove-Item (Join-Path $root "Bridge/Process/bin") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $root "Bridge/Process/obj") -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "  [$rid] -> $target"

    & $Dotnet publish $project `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $target `
        --nologo -v:q

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "publish failed for $rid; this RID is not staged"
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        continue
    }

    Get-ChildItem $target -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Host "Done. Bridges are under $outRoot"