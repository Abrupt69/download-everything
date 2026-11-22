# Download Everything

Une fenêtre simple pour télécharger des vidéos et de la musique sur Windows. Un seul fichier `.exe`, rien à installer.

Download Everything est une façade graphique pour [yt-dlp](https://github.com/yt-dlp/yt-dlp). On colle une adresse, on choisit le format, on télécharge.

## Ce que fait l'application

- Vidéo ou audio seul depuis les sites que yt-dlp sait lire (près de 1 800).
- Formats au choix. Par défaut un MP4 lisible partout (H.264 et AAC) pour la vidéo, et le MP3 pour l'audio. Aussi M4A, AAC, Opus, Vorbis, FLAC, WAV, AIFF et ALAC.
- Jaquette, titre, artiste et année inscrits dans le fichier. Noms de fichiers réguliers, « Titre.mp4 » ou « Artiste - Titre.mp3 ».
- Playlists et albums menés de 1 à 10 téléchargements à la fois (cinq par défaut), avec une ligne et une barre par élément.
- Plusieurs adresses collées d'un coup, traitées ensemble.
- Liens Spotify, Deezer et Apple Music (morceau, album, playlist). La liste des morceaux est lue dans le catalogue public du service, puis chaque morceau est cherché sur YouTube Music et YouTube. Aucun flux de ces services n'est lu.
- Qualité du son au choix, et un égaliseur à cinq préréglages (basses, aigus, basses et aigus, voix, volume égalisé) appliqué à chaque son téléchargé.
- Reprise sans doublon. Un morceau déjà présent dans le dossier n'est pas repris.
- Mesures en direct, débit, volume reçu, temps restant.
- Un bouton pour mettre à jour yt-dlp, FFmpeg, Deno et spotDL depuis leurs dépôts officiels, avec contrôle d'empreinte avant remplacement.

## Ce qu'elle ne fait pas

Les services à protection anti-copie (plateformes de films et de séries par abonnement, flux des services de musique) ne sont pas pris en charge et ne le seront pas.

Tidal, Qobuz et Amazon Music refusent la lecture de leurs listes par un programme. L'application le dit et s'arrête là.

Le son vient de YouTube, soit environ 128 à 160 kb/s. Un format sans perte (FLAC, WAV, AIFF, ALAC) ou un débit plus haut grossit le fichier sans améliorer ce qu'on entend.

## Longues playlists Spotify

La page publique de Spotify ne montre que les 100 premiers titres d'une playlist. Au-delà, deux voies.

- Pour vos propres playlists, « Compte Spotify » en bas de la fenêtre. Il faut créer une application sur le [tableau de bord des développeurs Spotify](https://developer.spotify.com/dashboard) (compte Premium exigé par Spotify), lui donner l'adresse de retour `http://127.0.0.1:8765/callback`, puis coller son identifiant dans l'application et se connecter. La liste complète est alors lue en quelques secondes. Le jeton de connexion reste sur votre poste, protégé par Windows.
- Pour les playlists des autres, spotDL lit la suite en arrière-plan pendant que les 100 premiers titres se téléchargent. C'est plus lent.

## Si YouTube bloque

Après beaucoup de demandes depuis la même connexion, YouTube peut refuser de répondre pendant quelques heures. L'application le reconnaît, s'arrête et affiche « BLOQUÉ ». Attendre, puis reprendre avec moins de téléchargements à la fois (3 à 5).

## Installation

1. Télécharger `DownloadEverything.exe` depuis la page [Releases](../../releases).
2. Le lancer. À la première ouverture, l'application dépose ses outils dans `%LOCALAPPDATA%\Download Everything`, cela prend quelques secondes.

L'exécutable n'est pas encore signé. Windows affiche donc un écran d'avertissement au premier lancement. Cliquer « Informations complémentaires », puis « Exécuter quand même ». L'empreinte SHA-256 du fichier est donnée dans les notes de chaque version, pour qui veut la vérifier.

Windows 10 ou 11, 64 bits.

## Usage responsable

Vous êtes responsable de ce que vous téléchargez. N'utilisez cet outil que pour des contenus que vous avez le droit de copier, les vôtres, ceux du domaine public ou sous licence libre, ou ceux dont la copie privée est permise par la loi de votre pays.

## Compiler soi-même

Il faut le SDK .NET 10 et PowerShell 7.

```powershell
# 1. Rassembler les outils à embarquer (téléchargés depuis leurs dépôts officiels, empreintes contrôlées)
pwsh -File preparer-charge.ps1

# 2. Publier l'exécutable unique
dotnet publish YtDlpInterface.csproj -c Release -o publication
```

L'exécutable se trouve ensuite dans `publication\DownloadEverything.exe`.

## Organisation du code

| Fichier | Rôle |
|---|---|
| `FenetrePrincipale.xaml` et `.xaml.cs` | la fenêtre, son habillage et son déroulé |
| `YtDlp.cs` | arguments et lancement de yt-dlp |
| `PageSpotify.cs`, `CompteSpotify.cs`, `SpotDl.cs` | lecture des liens Spotify (page publique, compte, spotDL en recours) |
| `AutresCatalogues.cs` | lecture des liens Deezer et Apple Music |
| `MiseAJourDesOutils.cs` | mise à jour des outils embarqués |
| `ChargeEmbarquee.cs`, `Charge/` | transport et dépôt des outils dans l'exécutable |
| `ArbreDeProcessus.cs` | arrêt propre des téléchargements |

Les commentaires du code sont en français.

## Licences

Le code de Download Everything est sous licence MIT, voir [LICENSE](LICENSE). Les outils embarqués gardent chacun leur licence, voir [NOTICES.md](NOTICES.md).
