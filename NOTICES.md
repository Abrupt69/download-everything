# Outils embarqués et leurs licences

Le code de Download Everything est sous licence MIT (voir `LICENSE`).

L'exécutable distribué dans les versions publiées transporte en plus six programmes tiers, inchangés, tels que leurs auteurs les publient. Ils sont déposés à la première ouverture dans `%LOCALAPPDATA%\Download Everything` et lancés comme des programmes séparés. Chacun reste soumis à sa propre licence.

| Programme | Rôle | Licence | Source officielle |
|---|---|---|---|
| yt-dlp | téléchargement de vidéo et d'audio | Unlicense (domaine public) | https://github.com/yt-dlp/yt-dlp |
| FFmpeg et ffprobe | assemblage et conversion | GPL version 3 ou ultérieure (construction « gpl-shared ») | https://github.com/yt-dlp/FFmpeg-Builds, code source sur https://ffmpeg.org |
| Deno | exécution du JavaScript dont yt-dlp a besoin pour YouTube | MIT | https://github.com/denoland/deno |
| spotDL | lecture de secours des liens Spotify | MIT | https://github.com/spotDL/spotify-downloader |
| gallery-dl | images et galeries d'images | GPL version 2 | https://codeberg.org/mikf/gallery-dl |
| aria2 | torrents et liens magnet | GPL version 2 ou ultérieure | https://github.com/aria2/aria2 |

## aria2

aria2 est distribué ici sous la licence publique générale GNU, version 2 ou ultérieure. Le texte de cette licence est déposé avec lui (`LICENCE-aria2.txt`). Le fichier binaire vient, sans modification, de l'archive `aria2-1.37.0-win-64bit-build1.zip` publiée sur le dépôt officiel, où se trouve aussi son code source. Ce dépôt ne publie pas d'empreinte. Celle de l'archive (SHA-256 `67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288`) a été relevée sur le dépôt officiel puis recoupée avec les registres Scoop et winget, qui donnent la même. Elle est fixée dans `preparer-charge.ps1`.

## gallery-dl

gallery-dl est distribué ici sous la licence publique générale GNU, version 2. Le fichier binaire vient, sans modification, des versions publiées par son auteur sur Codeberg, où se trouvent aussi son code source et le texte de sa licence (https://codeberg.org/mikf/gallery-dl/src/branch/master/LICENSE).

## FFmpeg

FFmpeg est distribué ici sous la licence publique générale GNU, version 3. Le texte de cette licence est déposé avec lui (`LICENCE-ffmpeg.txt`). Les fichiers binaires viennent, sans modification, de la version « latest » du dépôt yt-dlp/FFmpeg-Builds, qui publie aussi les scripts de construction. Le code source correspondant est celui de FFmpeg, disponible sur https://ffmpeg.org/download.html et https://github.com/FFmpeg/FFmpeg.

## Version Linux

Le paquet `.deb` ne transporte aucun de ces programmes. ffmpeg et aria2 sont installés par le système, comme dépendances du paquet. yt-dlp, Deno et gallery-dl sont téléchargés par l'application à sa première ouverture, depuis les mêmes dépôts officiels, avec le même contrôle d'empreinte. Le paquet contient en revanche l'environnement .NET et la bibliothèque d'interface Avalonia, tous deux sous licence MIT, ainsi que SkiaSharp et HarfBuzzSharp (licence MIT) dont Avalonia a besoin pour dessiner.

## Vérification

Le script `preparer-charge.ps1` télécharge chaque programme depuis son dépôt officiel et compare son empreinte SHA-256 à celle que ce dépôt publie. Rien n'est embarqué si une empreinte diffère.
