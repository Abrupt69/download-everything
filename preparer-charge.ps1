# Rassemble dans Charge\fichiers les programmes que l'exécutable embarque : yt-dlp, ffmpeg, ffprobe et Deno.
# Chacun vient de son dépôt officiel sur GitHub et son empreinte SHA-256 est comparée à celle que le dépôt publie.
# Si une empreinte diffère, le script s'arrête et rien n'est gardé.
# À relancer pour embarquer des versions plus récentes, puis republier.  pwsh -File preparer-charge.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$charge = Join-Path $PSScriptRoot 'Charge\fichiers'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("charge-yt-dlp-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $charge, $tmp | Out-Null
$h = @{ 'User-Agent' = 'curl' }   # GitHub refuse les demandes sans nom de client

function Get-Version([string]$depot) { Invoke-RestMethod "https://api.github.com/repos/$depot/releases/latest" -Headers $h }
function Get-Texte([string]$url) {
    $contenu = (Invoke-WebRequest -Uri $url -Headers $h).Content
    if ($contenu -is [byte[]]) { $contenu = [Text.Encoding]::UTF8.GetString($contenu) }
    return ($contenu -replace "`0", '')
}
function Get-Fichier($version, [string]$nom) {
    $piece = $version.assets | Where-Object name -eq $nom
    if (-not $piece) { throw "$nom absent de la version publiée" }
    $cible = Join-Path $tmp $nom
    Invoke-WebRequest -Uri $piece.browser_download_url -OutFile $cible -Headers $h
    return $cible
}
function Confirm-Empreinte([string]$fichier, [string]$attendue, [string]$quoi) {
    $reelle = (Get-FileHash $fichier -Algorithm SHA256).Hash.ToLower()
    if ($attendue.Length -ne 64 -or $attendue.ToLower() -ne $reelle) { throw "Empreinte de $quoi différente (attendue $attendue, calculée $reelle)." }
    "  $quoi, empreinte vérifiée $reelle"
}
function Get-EmpreinteDeListe([string]$liste, [string]$nom) {
    $ligne = $liste -split "`n" | Where-Object { $_ -match "\s\*?$([regex]::Escape($nom))\s*$" } | Select-Object -First 1
    return (($ligne -split '\s+')[0])
}

try {
    # yt-dlp
    $v = Get-Version 'yt-dlp/yt-dlp'
    "yt-dlp $($v.tag_name)"
    $f = Get-Fichier $v 'yt-dlp.exe'
    $sommes = Get-Texte (($v.assets | Where-Object name -eq 'SHA2-256SUMS').browser_download_url)
    Confirm-Empreinte $f (Get-EmpreinteDeListe $sommes 'yt-dlp.exe') 'yt-dlp.exe'
    Copy-Item $f (Join-Path $charge 'yt-dlp.exe') -Force

    # ffmpeg et ffprobe, variante à bibliothèques partagées, bien plus légère que la variante d'un seul bloc
    $v = Get-Version 'yt-dlp/FFmpeg-Builds'
    $nom = 'ffmpeg-master-latest-win64-gpl-shared.zip'
    "ffmpeg ($nom, publié le $($v.published_at))"
    $f = Get-Fichier $v $nom
    $sommes = Get-Texte (($v.assets | Where-Object name -eq 'checksums.sha256').browser_download_url)
    Confirm-Empreinte $f (Get-EmpreinteDeListe $sommes $nom) $nom
    $archive = [IO.Compression.ZipFile]::OpenRead($f)
    try {
        foreach ($entree in $archive.Entries) {
            $dansBin = $entree.FullName -match '/bin/[^/]+\.(exe|dll)$' -and $entree.Name -ne 'ffplay.exe'
            $licence = $entree.Name -eq 'LICENSE.txt'
            if (-not ($dansBin -or $licence)) { continue }
            $cible = if ($licence) { 'LICENCE-ffmpeg.txt' } else { $entree.Name }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entree, (Join-Path $charge $cible), $true)
        }
    } finally { $archive.Dispose() }

    # Deno, dont yt-dlp a besoin pour YouTube
    $v = Get-Version 'denoland/deno'
    $nom = 'deno-x86_64-pc-windows-msvc.zip'
    "Deno $($v.tag_name)"
    $f = Get-Fichier $v $nom
    $texte = Get-Texte (($v.assets | Where-Object name -eq "$nom.sha256sum").browser_download_url)
    if ($texte -notmatch '(?i)\b([0-9a-f]{64})\b') { throw 'Empreinte de Deno illisible.' }
    Confirm-Empreinte $f $Matches[1] $nom
    $archive = [IO.Compression.ZipFile]::OpenRead($f)
    try {
        $entree = $archive.Entries | Where-Object Name -eq 'deno.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entree, (Join-Path $charge 'deno.exe'), $true)
    } finally { $archive.Dispose() }
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# spotDL, pour les liens Spotify. Son dépôt ne publie pas de fichier d'empreintes,
# on compare donc à celle que GitHub calcule et affiche pour chaque fichier (champ digest).
$spotdl = Get-Version 'spotDL/spotify-downloader'
$piece = $spotdl.assets | Where-Object { $_.name -like 'spotdl-*-win32.exe' }
"spotDL $($spotdl.tag_name)"
$tmp2 = Join-Path ([IO.Path]::GetTempPath()) ("charge-spotdl-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp2 | Out-Null
try {
    if ($piece.browser_download_url -notlike 'https://github.com/spotDL/spotify-downloader/releases/download/*') { throw 'Adresse de spotDL inattendue.' }
    Invoke-WebRequest -Uri $piece.browser_download_url -OutFile "$tmp2\spotdl.exe" -Headers $h
    Confirm-Empreinte "$tmp2\spotdl.exe" ($piece.digest -replace '^sha256:', '') $piece.name
    Copy-Item "$tmp2\spotdl.exe" (Join-Path $charge 'spotdl.exe') -Force
} finally { Remove-Item -LiteralPath $tmp2 -Recurse -Force -ErrorAction SilentlyContinue }

# Versions embarquées, au format que lit le bouton « Tout mettre à jour » : la date de l'archive pour ffmpeg
# (son dépôt republie la même étiquette à chaque construction), le numéro pour Deno et pour spotDL.
$ffmpeg = (Get-Version 'yt-dlp/FFmpeg-Builds').assets | Where-Object name -eq 'ffmpeg-master-latest-win64-gpl-shared.zip'
$deno = (Get-Version 'denoland/deno').tag_name
@("ffmpeg=$($ffmpeg.updated_at.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))", "deno=$deno", "spotdl=$($spotdl.tag_name)") | Set-Content (Join-Path $charge 'versions.txt') -Encoding utf8

"--- Contenu de $charge"
Get-ChildItem $charge | ForEach-Object { "{0,-28} {1,8:N1} Mo" -f $_.Name, ($_.Length / 1MB) }
"Total {0:N0} Mo" -f ((Get-ChildItem $charge | Measure-Object Length -Sum).Sum / 1MB)
