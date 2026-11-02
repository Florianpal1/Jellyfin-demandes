<#
.SYNOPSIS
    Compile le plugin en Release et prépare le dossier à copier dans le répertoire « plugins » de Jellyfin.

.PARAMETER PluginsPath
    Facultatif : dossier « plugins » du serveur Jellyfin (ex. \\nas\docker\jellyfin\config\plugins).
    Si renseigné, le plugin y est copié directement (redémarrer Jellyfin ensuite).

.PARAMETER Version
    Facultatif : version du plugin (« 1.2.3 », « v1.2.3 » ou « 1.2.3.4 »). Par défaut : celle du .csproj.
    Utilisé par la CI avec le tag de la release GitHub.

.EXAMPLE
    ./scripts/package.ps1
    ./scripts/package.ps1 -PluginsPath "\\nas\docker\jellyfin\config\plugins"
    ./scripts/package.ps1 -Version v0.2.0
#>
param(
    [string]$PluginsPath,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Jellyfin.Plugin.C411Torbox/Jellyfin.Plugin.C411Torbox.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version invalide : « $Version » (attendu : 1.2.3 ou v1.2.3)."
}
# Jellyfin attend une version à 4 composantes.
$version = if ($Version.Split('.').Count -eq 3) { "$Version.0" } else { $Version }
$folderName = "C411 Torbox_$version"
$out = Join-Path $root "artifacts/$folderName"
$publish = Join-Path $root 'artifacts/publish'

Write-Host 'Tests…'
dotnet test (Join-Path $root 'Jellyfin.Plugin.C411Torbox.slnx') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Les tests ont échoué : paquet non généré.' }

Write-Host 'Compilation…'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $project -c Release -o $publish --nologo -v q "-p:Version=$version" "-p:AssemblyVersion=$version" "-p:FileVersion=$version"
if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $publish 'Jellyfin.Plugin.C411Torbox.dll') $out

$meta = [ordered]@{
    category    = 'General'
    changelog   = 'Première version.'
    description = 'Recherche de torrents C411 (via Prowlarr), demandes utilisateurs avec validation admin et envoi vers Torbox.'
    guid        = 'a1beec8b-cc16-4382-96ca-8d4cdbfd032c'
    name        = 'C411 Torbox'
    overview    = 'Demandes de films et séries C411 envoyées vers Torbox'
    owner       = 'flori'
    targetAbi   = '12.2.0.0'
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    version     = $version
    status      = 'Active'
    autoUpdate  = $false
    assemblies  = @('Jellyfin.Plugin.C411Torbox.dll')
}
# UTF-8 sans BOM.
[IO.File]::WriteAllText((Join-Path $out 'meta.json'), ($meta | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $root "artifacts/c411-torbox_$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip

Write-Host "Plugin prêt : $out"
Write-Host "Archive     : $zip"

if ($PluginsPath) {
    $target = Join-Path $PluginsPath $folderName
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $out '*') $target -Force
    Write-Host "Copié dans  : $target  → redémarrez Jellyfin."
}
