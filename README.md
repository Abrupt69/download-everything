# Download Everything

Une fenêtre simple pour télécharger des vidéos, de la musique, des images et des torrents. Sous Windows, un seul fichier `.exe`, rien à installer. Sous Debian et Ubuntu, un paquet `.deb`.

Download Everything est une façade graphique pour [yt-dlp](https://github.com/yt-dlp/yt-dlp). On colle une adresse, on choisit le format, on télécharge.

## Ce que fait l'application

- Vidéo ou audio seul depuis les sites que yt-dlp sait lire (près de 1 800).
- Formats au choix. Par défaut un MP4 lisible partout (H.264 et AAC) pour la vidéo, et le MP3 pour l'audio. Aussi M4A, AAC, Opus, Vorbis, FLAC, WAV, AIFF et ALAC.
- Jaquette, titre, artiste et année inscrits dans le fichier. Noms de fichiers réguliers, « Titre.mp4 » ou « Artiste - Titre.mp3 ».
- Playlists et albums menés de 1 à 10 téléchargements à la fois (cinq par défaut), avec une ligne et une barre par élément.
- Plusieurs adresses collées d'un coup, traitées ensemble.
- Liens Spotify, Deezer et Apple Music (morceau, album, playlist). La liste des morceaux est lue dans le catalogue public du service, puis chaque morceau est cherché sur YouTube Music et YouTube. Aucun flux de ces services n'est lu.
- Qualité du son au choix, et un égaliseur à cinq préréglages (basses, aigus, basses et aigus, voix, volume égalisé) appliqué à chaque son téléchargé.
- Images et galeries d'images. Quand yt-dlp ne trouve ni vidéo ni son à une adresse, [gallery-dl](https://codeberg.org/mikf/gallery-dl) prend le relais, sans rien demander. Les images sont rangées dans un sous-dossier au nom du site.
- Torrents, dans leur propre vue. Deux touches en haut de la fenêtre, « Téléchargement » et « Torrents », changent la vue. Un lien magnet, l'adresse d'un fichier `.torrent` ou un fichier `.torrent` glissé sur la fenêtre sont pris par [aria2](https://github.com/aria2/aria2). La vue montre pour chaque torrent son état, son débit, ses sources (seeders) et ses pairs (leechers), puis ses fichiers et les pairs reliés avec leur logiciel. Pause, reprise et retrait. Les torrents continuent pendant qu'on télécharge autre chose, et rien n'est partagé une fois un torrent fini. Rajouter le même torrent le reprend où il s'était arrêté.
- Fichiers ordinaires. Une adresse qui mène à un PDF, une archive ou un document est téléchargée telle quelle.
- Reprise sans doublon. Un morceau déjà présent dans le dossier n'est pas repris.
- Reprise des manquants. À la fin d'une liste, un bouton relance seulement ce qui n'a pas abouti, et les titres introuvables sont notés dans « Titres manquants.txt ».
- Morceaux à vérifier. Un morceau est retenu sur sa durée, et deux morceaux différents peuvent durer pareil. Quand le titre de la vidéo retenue ne ressemble pas au titre demandé, ou qu'elle s'annonce comme un live, un remix ou une reprise, le morceau est marqué « à vérifier » et noté dans « Titres à vérifier.txt ». Rien n'est refusé, c'est à l'oreille de trancher.
- Un lien se glisse sur la fenêtre, ou se copie simplement. Si le presse-papiers ne contient que des adresses web, elles sont placées dans le champ quand la fenêtre revient au premier plan. Rien n'est lancé sans clic.
- Fenêtre réglable. Tirer un côté fait grossir ou rétrécir tout le contenu, la taille est retenue.
- Mesures en direct, débit, volume reçu, temps restant.
- Un bouton pour mettre à jour yt-dlp, FFmpeg, Deno, spotDL et gallery-dl depuis leurs dépôts officiels, avec contrôle d'empreinte avant remplacement.
- L'application signale quand une nouvelle version d'elle-même est publiée ici, et l'installe en un clic après contrôle d'empreinte. Pour cela elle interroge la page des versions de ce dépôt à chaque ouverture, sans rien envoyer d'autre que cette demande.

## Ce qu'elle ne fait pas

Les services à protection anti-copie (plateformes de films et de séries par abonnement, flux des services de musique) ne sont pas pris en charge et ne le seront pas.

Tidal, Qobuz et Amazon Music refusent la lecture de leurs listes par un programme. L'application le dit et s'arrête là.

L'application ne se connecte à aucun compte. Les publications publiques des réseaux sociaux passent en général (mesuré le 22/11/2026 sur des publications publiques, sans compte : Instagram, TikTok et X trois fois sur quatre). Ce qui exige d'être connecté ne passe pas : Reddit, les comptes privés, les contenus réservés aux abonnés.

## Torrents et pare-feu

À la première utilisation d'un torrent, Windows peut afficher une alerte de pare-feu au nom de `aria2c`. Un outil de torrent écoute le réseau pour que d'autres puissent le joindre, c'est ce que Windows signale. Refuser l'alerte n'empêche pas de télécharger.

Un torrent occupe sa taille entière sur le disque dès ses premiers morceaux, c'est le fonctionnement normal de ce protocole. Un torrent que plus personne ne partage reste sur « recherche de sources », il n'y a alors rien à faire de plus.

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

## Installation sous Linux (Debian, Ubuntu)

1. Télécharger `download-everything_1.2.1_amd64.deb` depuis la page [Releases](../../releases).
2. L'installer, ce qui amène aussi ce dont il a besoin (ffmpeg, aria2, les bibliothèques graphiques) :

```
sudo apt install ./download-everything_1.2.1_amd64.deb
```

L'application apparaît dans le menu sous le nom « Download Everything ». À la première ouverture elle télécharge yt-dlp, Deno et gallery-dl depuis leurs dépôts officiels, empreinte contrôlée, et les range dans `~/.local/share/download-everything`. Il faut donc être connecté à Internet ce jour-là.

Ce qui diffère de la version Windows :

- ffmpeg et aria2 sont ceux du système, tenus à jour par lui. Le bouton « Tout mettre à jour » ne s'occupe que de yt-dlp, Deno et gallery-dl.
- Une nouvelle version de l'application est signalée, mais elle s'installe en téléchargeant le nouveau paquet, l'application ne peut pas se remplacer elle-même dans un dossier du système.
- Pas encore de glisser-déposer, ni de lecture par spotDL de la suite des playlists Spotify de plus de 100 titres.
- La fenêtre garde la taille de son dessin.
- Le jeton du compte Spotify, si vous vous connectez, est gardé dans un fichier que seul votre compte peut lire. Linux n'a pas d'équivalent au chiffrement de Windows lié au compte.

Vérifié le 22/11/2026 dans des conteneurs Debian 13 et Ubuntu 24.04 vierges : installation, téléchargement des outils, vidéo, son, image, PDF, torrent, et pilotage de la vraie fenêtre sur un écran virtuel. Pas encore essayé sur un vrai bureau Linux.

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

Pour le paquet Linux, il faut en plus Docker. La compilation se fait sur place, l'emballage dans un conteneur Debian jetable :

```powershell
pwsh -File linux\construire-deb.ps1
```

Le paquet se trouve ensuite dans `linux\publication\`.

## Organisation du code

| Fichier | Rôle |
|---|---|
| `FenetrePrincipale.xaml` et `.xaml.cs` | la fenêtre, son habillage et son déroulé |
| `YtDlp.cs` | arguments et lancement de yt-dlp |
| `PageSpotify.cs`, `CompteSpotify.cs`, `SpotDl.cs` | lecture des liens Spotify (page publique, compte, spotDL en recours) |
| `AutresCatalogues.cs` | lecture des liens Deezer et Apple Music |
| `GalleryDl.cs` | images et galeries, second outil de la cascade |
| `FichierDirect.cs` | fichier ordinaire pris tel quel, dernier recours de la cascade |
| `Aria2.cs` | torrents et liens magnet : aria2 lancé en service et interrogé chaque seconde |
| `VueTorrents.xaml` et `.xaml.cs` | la vue des torrents, avec sources, pairs et fichiers |
| `VersionDeLApplication.cs` | avis de nouvelle version et remplacement de l'exécutable |
| `MiseAJourDesOutils.cs` | mise à jour des outils embarqués |
| `ChargeEmbarquee.cs`, `Charge/` | transport et dépôt des outils dans l'exécutable |
| `ArbreDeProcessus.cs` | arrêt propre des téléchargements |

| `linux/` | la version Linux : son dessin avec Avalonia (`.axaml`), l'installation des outils, la fiche et le script du paquet `.deb` |

Le code des deux fenêtres (`FenetrePrincipale.xaml.cs`, `VueTorrents.xaml.cs`) et tout le moteur sont communs aux deux versions. Les passages propres à l'une ou à l'autre sont entre `#if PORTABLE` et `#endif`, et `linux/CompatWpf.cs` donne à Avalonia les quelques noms de WPF dont ce code se sert. Seuls les dessins diffèrent : `.xaml` pour Windows, `.axaml` pour Linux.

Les commentaires du code sont en français.

## Licences

Le code de Download Everything est sous licence MIT, voir [LICENSE](LICENSE). Les outils embarqués gardent chacun leur licence, voir [NOTICES.md](NOTICES.md).
