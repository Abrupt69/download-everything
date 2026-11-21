# Download Everything

Une fenêtre simple pour télécharger des vidéos et de la musique sur Windows. Un seul fichier `.exe`, rien à installer.

Download Everything est une façade graphique pour [yt-dlp](https://github.com/yt-dlp/yt-dlp). On colle une adresse, on choisit le format, on télécharge.

## Ce que fait l'application

- Vidéo ou audio seul depuis les sites que yt-dlp sait lire (près de 1 800).
- Formats au choix. Par défaut un MP4 lisible partout (H.264 et AAC) pour la vidéo, et le MP3 pour l'audio.
- Jaquette, titre, artiste et année inscrits dans le fichier. Noms de fichiers réguliers, « Titre.mp4 » ou « Artiste - Titre.mp3 ».
- Playlists et albums menés cinq téléchargements à la fois, avec une ligne et une barre par élément.
- Plusieurs adresses collées d'un coup, traitées ensemble.
- Liens Spotify (morceau, album, playlist). La liste des morceaux est lue sur la page publique de Spotify, puis chaque morceau est cherché sur YouTube Music et YouTube. Aucun flux de Spotify n'est lu.
- Mesures en direct, débit, volume reçu, temps restant.
- Un bouton pour mettre à jour yt-dlp, FFmpeg, Deno et spotDL depuis leurs dépôts officiels, avec contrôle d'empreinte avant remplacement.

## Ce qu'elle ne fait pas

Les services à protection anti-copie (plateformes de films et de séries par abonnement, flux des services de musique) ne sont pas pris en charge et ne le seront pas.

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
| `PageSpotify.cs`, `SpotDl.cs` | lecture des liens Spotify |
| `MiseAJourDesOutils.cs` | mise à jour des outils embarqués |
| `ChargeEmbarquee.cs`, `Charge/` | transport et dépôt des outils dans l'exécutable |
| `ArbreDeProcessus.cs` | arrêt propre des téléchargements |

Les commentaires du code sont en français.

## Licences

Le code de Download Everything est sous licence MIT, voir [LICENSE](LICENSE). Les outils embarqués gardent chacun leur licence, voir [NOTICES.md](NOTICES.md).
