<#
.SYNOPSIS
  Baut Paddock komplett: Tests, Publish (self-contained, win-x64), Beispiel-Plugin, MSI und portables ZIP.
.EXAMPLE
  ./build/build.ps1 -Version 1.4.2
#>
param(
    [string]$Version = "0.0.0-dev",
    [string]$Configuration = "Release",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$pluginTmp = Join-Path $artifacts "plugin-build"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $artifacts | Out-Null

# MSI-Versionen müssen rein numerisch sein (major.minor.patch)
$numeric = ($Version -split '[-+]')[0]

function Invoke-Step([string]$name, [scriptblock]$block) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name fehlgeschlagen (Exit $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Invoke-Step "Tests" { dotnet test (Join-Path $root "tests/Paddock.Tests") -c $Configuration --nologo }
}

Invoke-Step "Publish App" {
    dotnet publish (Join-Path $root "src/Paddock") -c $Configuration -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:Version=$numeric -p:InformationalVersion=$Version -o $publish --nologo
}

Invoke-Step "Beispiel-Plugin" {
    dotnet build (Join-Path $root "plugins/Paddock.Plugins.Examples") -c $Configuration -p:Version=$numeric -o $pluginTmp --nologo
}
$pluginDir = Join-Path $publish "plugins/Examples"
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item (Join-Path $pluginTmp "Paddock.Plugins.Examples.dll"), (Join-Path $pluginTmp "Paddock.Plugins.Examples.deps.json") $pluginDir
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item

Invoke-Step "MSI" {
    dotnet build (Join-Path $root "installer/Paddock.Installer.wixproj") -c $Configuration `
        -p:Version=$numeric "-p:PublishDir=$publish\" -o (Join-Path $artifacts "msi") --nologo
}

$zip = Join-Path $artifacts "Paddock-$numeric-x64-portable.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip
$msi = Get-ChildItem (Join-Path $artifacts "msi") -Filter *.msi | Select-Object -First 1
Copy-Item $msi.FullName $artifacts
Write-Host "Fertig:" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
