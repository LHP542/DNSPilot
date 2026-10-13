# Bringt ein fertiges GitHub-Release auf den Update-Share, von dem die App ihre
# Updates bezieht.
#
#   scripts\publish-share.ps1 -Version 0.2.0 -Source $HOME\Downloads
#   scripts\publish-share.ps1 -Version 0.2.0                          # lädt selbst per gh
#   scripts\publish-share.ps1 -Version 0.2.0 -Source … -Share D:\Test  # Probelauf
#
# -Source: Ordner, in den die Release-Dateien von Hand geladen wurden. Auf dem
# Arbeitslaptop ist das der normale Weg — der Firmen-Proxy blockt .zip-Dateien
# von GitHub (403, Inhaltsfilter), also über den Citrix-Browser laden.
# Ohne -Source versucht das Skript den Download per gh.
#
# Jede Datei wird gegen die SHA-256-Prüfsumme geprüft, die GitHub zum Release
# speichert (die Metadaten kommen durch den Proxy). Auf dem Share landet damit
# genau das, was die CI gebaut hat — kein lokal gebautes, kein falsches Paket.
#
# Reihenfolge und Kopierweg sind Absicht:
#   1. ZUERST die Notes — sonst sieht ein Client, der genau dazwischen prüft,
#      das neue Paket ohne Versionshinweise.
#   2. Pakete erst unter .tmp-Namen kopieren und dann umbenennen. DNS-Pilot sucht
#      nur nach *-win-x64.zip; ein halb kopiertes Paket ist für sie unsichtbar.
#
# Voraussetzung: gh ist angemeldet und hat Lesezugriff auf LHP542/DNSPilot.

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Source,

    [string]$Share = '\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot',

    [string]$Repo = 'LHP542/DNSPilot'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Share)) {
    throw "Share nicht erreichbar: $Share"
}

# Was gehört zum Release, mit welcher Prüfsumme?
$json = gh api "repos/$Repo/releases/tags/v$Version"
if ($LASTEXITCODE -ne 0) { throw "Release v$Version in $Repo nicht gefunden." }
$assets = ($json | ConvertFrom-Json).assets |
    Where-Object { $_.name -like "DnsPilot-$Version-*" }

$notesName = "DnsPilot-$Version-notes.md"
$zipName = "DnsPilot-$Version-win-x64.zip"

foreach ($pflicht in @($notesName, $zipName)) {
    if (-not ($assets | Where-Object { $_.name -eq $pflicht })) {
        throw "Zum Release gehört kein $pflicht — so wird nicht veröffentlicht."
    }
}

if (-not $Source) {
    $Source = Join-Path ([System.IO.Path]::GetTempPath()) "DnsPilot-publish-$Version"
    if (Test-Path $Source) { Remove-Item $Source -Recurse -Force }
    New-Item -ItemType Directory -Path $Source | Out-Null

    Write-Host "Lade Release v$Version aus $Repo …"
    gh release download "v$Version" --repo $Repo --dir $Source --pattern "DnsPilot-$Version-*"
    if ($LASTEXITCODE -ne 0) {
        throw ("Download fehlgeschlagen. Auf dem Arbeitslaptop blockt der Proxy .zip — " +
               "die Dateien über den Citrix-Browser laden und mit -Source <Ordner> erneut aufrufen.")
    }
}

# Prüfen, BEVOR irgendetwas auf den Share geht.
$geprueft = @()
foreach ($asset in $assets) {
    $datei = Join-Path $Source $asset.name

    if (-not (Test-Path -LiteralPath $datei)) {
        if ($asset.name -in @($notesName, $zipName)) {
            throw "$($asset.name) fehlt in $Source."
        }
        Write-Warning "$($asset.name) fehlt in $Source — wird ausgelassen."
        continue
    }

    $soll = ($asset.digest -replace '^sha256:', '').ToLowerInvariant()
    $ist = (Get-FileHash -LiteralPath $datei -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ist -ne $soll) {
        throw "$($asset.name): Prüfsumme stimmt nicht mit dem GitHub-Release überein — nicht veröffentlicht."
    }

    Write-Host "geprüft: $($asset.name)"
    $geprueft += Get-Item -LiteralPath $datei
}

# 1. Notes zuerst.
$notes = $geprueft | Where-Object { $_.Name -eq $notesName }
Copy-Item -LiteralPath $notes.FullName -Destination $Share -Force
Write-Host "kopiert: $notesName"

# 2. Pakete über einen Zwischennamen.
foreach ($paket in ($geprueft | Where-Object { $_.Name -ne $notesName })) {
    $ziel = Join-Path $Share $paket.Name
    $zwischen = "$ziel.tmp"
    Copy-Item -LiteralPath $paket.FullName -Destination $zwischen -Force
    Move-Item -LiteralPath $zwischen -Destination $ziel -Force
    Write-Host "kopiert: $($paket.Name) ($([math]::Round($paket.Length / 1MB, 1)) MB)"
}

Write-Host ""
Write-Host "Version $Version liegt in $Share."
Write-Host "Laufende DNS-Pilot-Instanzen melden das Update binnen 30 Minuten, neue beim Start."
