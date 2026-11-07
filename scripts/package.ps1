<#
.SYNOPSIS
    Compile le plugin en Release et prépare le dossier à copier dans le répertoire « plugins » de Jellyfin.

.PARAMETER PluginsPath
    Facultatif : dossier « plugins » du serveur Jellyfin (ex. \\nas\docker\jellyfin\config\plugins).
    Si renseigné, le plugin y est copié directement (redémarrer Jellyfin ensuite).

.PARAMETER Version
    Facultatif : version du plugin (« 1.2.3 », « v1.2.3 » ou « 1.2.3.4 »). Par défaut : celle du .csproj.
    Utilisé par la CI avec le tag de la release GitHub.

.PARAMETER SourceUrlBase
    Facultatif : adresse du dossier où l'archive zip sera publiée. Si renseignée, un « manifest.json » de dépôt
    de plugins Jellyfin est aussi généré (Tableau de bord → Extensions → Dépôts), pointant vers
    <SourceUrlBase>/c411-torbox_<version>.zip. Jellyfin doit pouvoir lire le manifest et l'archive sans authentification.

.PARAMETER Changelog
    Facultatif : notes de version, reprises dans meta.json et manifest.json.

.EXAMPLE
    ./scripts/package.ps1
    ./scripts/package.ps1 -PluginsPath "\\nas\docker\jellyfin\config\plugins"
    ./scripts/package.ps1 -Version v0.2.0
    ./scripts/package.ps1 -SourceUrlBase "http://nas.local/jellyfin-plugins"
#>
param(
    [string]$PluginsPath,
    [string]$Version,
    [string]$SourceUrlBase,
    [string]$Changelog = 'Voir les notes de la release GitHub.'
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

$guid = 'a1beec8b-cc16-4382-96ca-8d4cdbfd032c'
$name = 'C411 Torbox'
$description = 'Recherche de torrents C411 (en direct ou via Prowlarr), demandes utilisateurs avec validation admin, envoi vers Torbox puis téléchargement et rangement dans la bibliothèque.'
$overview = 'Demandes de films, séries et livres C411 envoyées vers Torbox'
$owner = 'flori'
$targetAbi = '12.2.0.0'
$timestamp = (Get-Date).ToUniversalTime().ToString('o')

$meta = [ordered]@{
    category    = 'General'
    changelog   = $Changelog
    description = $description
    guid        = $guid
    name        = $name
    overview    = $overview
    owner       = $owner
    targetAbi   = $targetAbi
    timestamp   = $timestamp
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

if ($SourceUrlBase) {
    # Dépôt de plugins Jellyfin : une seule version (la dernière), somme MD5 de l'archive.
    $manifest = @(
        [ordered]@{
            guid        = $guid
            name        = $name
            description = $description
            overview    = $overview
            owner       = $owner
            category    = 'General'
            versions    = @(
                [ordered]@{
                    version   = $version
                    changelog = $Changelog
                    targetAbi = $targetAbi
                    sourceUrl = $SourceUrlBase.TrimEnd('/') + "/c411-torbox_$version.zip"
                    checksum  = (Get-FileHash -Algorithm MD5 $zip).Hash.ToLowerInvariant()
                    timestamp = $timestamp
                }
            )
        }
    )
    $manifestPath = Join-Path $root 'artifacts/manifest.json'
    [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Manifest    : $manifestPath"
}

if ($PluginsPath) {
    $target = Join-Path $PluginsPath $folderName
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $out '*') $target -Force
    Write-Host "Copié dans  : $target  → redémarrez Jellyfin."
}
