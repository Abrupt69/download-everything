# Outils embarqués et leurs licences

Le code de Download Everything est sous licence MIT (voir `LICENSE`).

L'exécutable distribué dans les versions publiées transporte en plus quatre programmes tiers, inchangés, tels que leurs auteurs les publient. Ils sont déposés à la première ouverture dans `%LOCALAPPDATA%\Download Everything` et lancés comme des programmes séparés. Chacun reste soumis à sa propre licence.

| Programme | Rôle | Licence | Source officielle |
|---|---|---|---|
| yt-dlp | téléchargement de vidéo et d'audio | Unlicense (domaine public) | https://github.com/yt-dlp/yt-dlp |
| FFmpeg et ffprobe | assemblage et conversion | GPL version 3 ou ultérieure (construction « gpl-shared ») | https://github.com/yt-dlp/FFmpeg-Builds, code source sur https://ffmpeg.org |
| Deno | exécution du JavaScript dont yt-dlp a besoin pour YouTube | MIT | https://github.com/denoland/deno |
| spotDL | lecture de secours des liens Spotify | MIT | https://github.com/spotDL/spotify-downloader |

## FFmpeg

FFmpeg est distribué ici sous la licence publique générale GNU, version 3. Le texte de cette licence est déposé avec lui (`LICENCE-ffmpeg.txt`). Les fichiers binaires viennent, sans modification, de la version « latest » du dépôt yt-dlp/FFmpeg-Builds, qui publie aussi les scripts de construction. Le code source correspondant est celui de FFmpeg, disponible sur https://ffmpeg.org/download.html et https://github.com/FFmpeg/FFmpeg.

## Vérification

Le script `preparer-charge.ps1` télécharge chaque programme depuis son dépôt officiel et compare son empreinte SHA-256 à celle que ce dépôt publie. Rien n'est embarqué si une empreinte diffère.
