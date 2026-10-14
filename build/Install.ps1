<#
.SYNOPSIS
    Installiert den Window Rotator für den angemeldeten Benutzer (keine Administratorrechte nötig).
.DESCRIPTION
    Kopiert WindowRotator.exe nach %LOCALAPPDATA%\Programs\WindowRotator, legt eine
    Startmenü-Verknüpfung an und importiert die Beispielprofile, sofern noch kein gleichnamiges Profil existiert.
    -Autostart startet das Programm bei jeder Windows-Anmeldung im Infobereich.
    -Uninstall entfernt Programm, Verknüpfung und Autostart (Profile und Protokolle bleiben erhalten).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install.ps1 -Autostart
#>
param([switch]$Autostart, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$name = 'WindowRotator'
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe = Join-Path $target "$name.exe"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Window Rotator.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force

if ($Uninstall) {
    Remove-ItemProperty $runKey -Name $name -ErrorAction SilentlyContinue
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Write-Host 'Window Rotator wurde entfernt. Profile/Protokolle: %APPDATA% und %LOCALAPPDATA%\WindowRotator'
    return
}

New-Item $target -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot "$name.exe") $exe -Force

$profiles = Join-Path $env:APPDATA "$name\profiles"
New-Item $profiles -ItemType Directory -Force | Out-Null
Get-ChildItem (Join-Path $PSScriptRoot 'Beispielprofile') -Filter *.json -ErrorAction SilentlyContinue |
    Where-Object { -not (Test-Path (Join-Path $profiles $_.Name)) } | Copy-Item -Destination $profiles

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.Description = 'Automatisierte Fensterrotation für Produktionsmonitore'
$link.Save()

if ($Autostart) { Set-ItemProperty $runKey -Name $name -Value "`"$exe`" --tray" }

Write-Host "Installiert nach $target$(if ($Autostart) { ' (mit Autostart)' })."
Start-Process $exe
