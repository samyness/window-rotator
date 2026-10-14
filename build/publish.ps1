<#
.SYNOPSIS
    Baut das auslieferbare Self-contained-Paket des Window Rotator.
.DESCRIPTION
    1. Selbsttest (Fensterwechler.Tests) – bricht bei Fehlern ab.
    2. dotnet publish als einzelne, eigenständige EXE (keine .NET-Installation auf dem Ziel-PC nötig).
    3. Paket mit Beispielprofil, Anleitungen und Install.ps1 unter dist\ sowie als ZIP.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Fensterwechler\Fensterwechler.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
$package = Join-Path $dist 'WindowRotator'

dotnet run --project (Join-Path $root 'Fensterwechler.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Selbsttest fehlgeschlagen – kein Paket erstellt.' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $package
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fehlgeschlagen.' }

Copy-Item (Join-Path $root 'samples') (Join-Path $package 'Beispielprofile') -Recurse
Copy-Item (Join-Path $root 'docs\*.md') $package
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1') $package
Copy-Item (Join-Path $root 'LICENSE') $package

$zip = Join-Path $dist "WindowRotator-$version-$Runtime.zip"
Compress-Archive -Path "$package\*" -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $(Split-Path $zip -Leaf)" } | Set-Content "$zip.sha256" -Encoding ascii
Write-Host "Paket erstellt: $zip"
