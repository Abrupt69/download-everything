using System.Diagnostics;
using System.IO;
using System.Text;

namespace YtDlpInterface;

/// <summary>Une ligne d'une liste déroulante, le texte montré et la valeur passée à yt-dlp.</summary>
internal sealed record Choix(string Libelle, string Valeur)
{
    public override string ToString() => Libelle;
}

/// <summary>Ce que l'utilisateur a demandé dans la fenêtre.</summary>
/// <param name="PrefixeDeNom">
/// Ce qui précède le nom du fichier, déjà prêt à entrer dans un modèle de yt-dlp. Vide pour une vidéo seule.
/// Pour une vidéo de playlist, le dossier de la playlist et son rang, voir <see cref="YtDlp.PrefixeDeListe"/>.
/// </param>
/// <param name="Egaliseur">Filtres de ffmpeg à appliquer au son, vide pour aucun. Voir <see cref="YtDlp.Egaliseurs"/>.</param>
/// <param name="QualiteDuSon">Débit du son quand il est converti (MP3, M4A, Opus…), au format de yt-dlp. « 0 » vise le meilleur.</param>
internal sealed record Demande(
    string Adresse, bool AudioSeul, string Format, string Qualite, string Dossier, string PrefixeDeNom, string QualiteDuSon = "0",
    string Egaliseur = "");

/// <summary>Les programmes compagnons trouvés dans le dossier des outils, null quand ils manquent.</summary>
internal sealed record Complements(string? Ffmpeg, string? Deno);

/// <summary>
/// Tout ce qui touche à yt-dlp lui-même. Où il se trouve, quels arguments lui passer,
/// comment le lancer. La fenêtre ne connaît que cette classe.
/// </summary>
internal static class YtDlp
{
    public const string NomExecutable = "yt-dlp.exe";
    public const string PageOfficielle = "https://github.com/yt-dlp/yt-dlp/releases/latest";

    /// <summary>Valeur vide, yt-dlp garde le fichier tel que le site le fournit, sans conversion.</summary>
    public static readonly Choix FormatDOrigine = new("Format d'origine du site", "");

    // Les conteneurs que yt-dlp sait produire avec ffmpeg. En tête, le MP4 que tous les appareils lisent.
    public static readonly Choix[] FormatsVideo =
    [
        new("MP4 universel (H.264, AAC)", "mp4"),
        new("MP4, qualité maximale", "mp4max"),
        new("MKV", "mkv"),
        new("WebM", "webm"),
        new("MOV", "mov"),
        new("AVI (conversion, plus long)", "avi"),
        new("FLV (conversion, plus long)", "flv"),
        FormatDOrigine,
    ];

    public static readonly Choix[] FormatsAudio =
    [
        new("MP3, lisible partout", "mp3"),
        new("M4A", "m4a"),
        new("AAC", "aac"),
        new("Opus", "opus"),
        new("OGG Vorbis", "vorbis"),
        new("FLAC, sans perte", "flac"),
        new("WAV, sans perte", "wav"),
        new("AIFF, sans perte", "aiff"),
        new("ALAC, sans perte", "alac"),
        FormatDOrigine,
    ];

    // Égaliseur. Chaque réglage est une chaîne de filtres de ffmpeg, dont l'effet a été mesuré sur un bruit de
    // test (basses +6 dB, aigus +5 dB, voix +3 dB autour de 2 500 Hz). Un limiteur ferme les réglages qui
    // renforcent une bande, pour qu'un passage fort ne sature pas. « Volume égalisé » ne touche pas au timbre :
    // il ramène tous les morceaux au même niveau sonore, utile pour une playlist venue de sources diverses.
    public static readonly Choix[] Egaliseurs =
    [
        new("Aucun", ""),
        new("Basses renforcées", "bass=g=6:f=100,alimiter=limit=0.95"),
        new("Aigus renforcés", "treble=g=5:f=8000,alimiter=limit=0.95"),
        new("Basses et aigus", "bass=g=5:f=100,treble=g=4:f=8000,alimiter=limit=0.95"),
        new("Voix en avant", "equalizer=f=2500:t=q:w=1:g=4,bass=g=-2:f=100,alimiter=limit=0.95"),
        new("Volume égalisé", "loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000"),
    ];

    /// <summary>
    /// Les arguments qui disent à yt-dlp sous quelle forme rendre le son, une fois « -x » demandé.
    /// Trois cas sortent de l'ordinaire.
    /// L'AIFF n'est pas dans la liste des formats d'extraction de yt-dlp : le son est extrait, puis converti.
    /// L'ALAC doit être forcé, sinon yt-dlp rend de l'AAC dans le même type de fichier (vérifié aux essais).
    /// L'égaliseur oblige à réencoder. Pour ne pas ajouter une perte, le filtre est appliqué pendant une
    /// extraction sans perte (FLAC), et c'est ce résultat qui est converti vers le format demandé.
    /// </summary>
    private static List<string> SortieAudio(string format, string qualite, string egaliseur)
    {
        var filtre = egaliseur.Length > 0 ? $"-af {egaliseur}" : "";
        // Garder « le format d'origine » n'a pas de sens avec un égaliseur, le son étant réencodé de toute façon.
        // On rend alors de l'Opus, léger et fidèle.
        if (format.Length == 0 && filtre.Length > 0) format = "opus";

        switch (format)
        {
            case "":
                return [];
            case "flac" or "wav":
                return filtre.Length > 0 ? ["--audio-format", format, "--ppa", $"ExtractAudio:{filtre}"] : ["--audio-format", format];
            case "alac":
                return ["--audio-format", "alac", "--ppa", $"ExtractAudio:{(filtre.Length > 0 ? filtre + " " : "")}-acodec alac"];
            case "aiff" when filtre.Length == 0:
                return ["--recode-video", "aiff"];
        }

        if (filtre.Length == 0) return ["--audio-format", format, "--audio-quality", qualite];

        // Avec égaliseur : extraction en FLAC filtrée, puis conversion. « vorbis » s'appelle « ogg » à cette étape.
        var cible = format == "vorbis" ? "ogg" : format;
        var arguments = new List<string> { "--audio-format", "flac", "--ppa", $"ExtractAudio:{filtre}", "--recode-video", cible };
        var debit = qualite.EndsWith('K') ? qualite[..^1] + "k" : "";
        var reglage = cible switch
        {
            "mp3" => debit.Length > 0 ? $"-b:a {debit}" : "-q:a 0",
            "m4a" or "aac" => $"-b:a {(debit.Length > 0 ? debit : "256k")}",
            "opus" => $"-b:a {(debit.Length > 0 ? debit : "160k")}",
            "ogg" => debit.Length > 0 ? $"-b:a {debit}" : "-q:a 6",
            _ => "",
        };
        if (reglage.Length > 0) arguments.AddRange(["--ppa", $"VideoConvertor:{reglage}"]);
        return arguments;
    }

    // La valeur est la hauteur d'image maximale. Vide, yt-dlp prend la plus haute que le site propose.
    public static readonly Choix[] Qualites =
    [
        new("La meilleure disponible", ""),
        new("2160p (4K)", "2160"),
        new("1440p", "1440"),
        new("1080p (Full HD)", "1080"),
        new("720p (HD)", "720"),
        new("480p", "480"),
        new("360p", "360"),
    ];

    /// <summary>
    /// Dossier du profil où vivent yt-dlp.exe, ses compagnons (ffmpeg, ffprobe, Deno) et les réglages.
    /// Il est dans le profil de l'utilisateur, donc yt-dlp peut s'y mettre à jour sans droits d'administrateur.
    /// Les programmes ne sont cherchés que là. Un fichier du même nom posé ailleurs, par exemple
    /// dans Téléchargements à côté de l'exécutable, n'est jamais lancé à leur place.
    /// </summary>
    public static string DossierOutils { get; } =
        // Aide aux essais. Cette variable d'environnement déplace le dossier, ce qui permet d'essayer une nouvelle
        // version dans une copie à part, avec ses propres outils et réglages, sans gêner une application déjà ouverte.
        Environment.GetEnvironmentVariable("DOWNLOAD_EVERYTHING_DOSSIER") is { Length: > 0 } ailleurs && Path.IsPathFullyQualified(ailleurs)
            ? ailleurs
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Download Everything");

    /// <summary>Marque en tête des lignes de mesure que yt-dlp écrit pour l'interface, voir le gabarit ci-dessous.</summary>
    public const string MarqueDeMesure = "DE|";

    // Gabarit de progression. yt-dlp écrit une ligne de chiffres bruts à chaque mise à jour, dans cet ordre :
    // état, octets reçus, octets au total, total estimé, débit, secondes restantes, secondes écoulées,
    // fragment en cours, nombre de fragments, protocole, définition de l'image, puis le titre.
    // Une valeur inconnue vaut « NA ». Le titre vient en dernier parce qu'il peut lui-même contenir une barre verticale.
    // Lire des chiffres est plus sûr que de déchiffrer une phrase prévue pour l'œil.
    public const int NombreDeChampsDeMesure = 13;
    private const string GabaritDeMesure =
        "download:" + MarqueDeMesure
        + "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s"
        + "|%(progress.speed)s|%(progress.eta)s|%(progress.elapsed)s"
        + "|%(progress.fragment_index)s|%(progress.fragment_count)s|%(info.protocol)s|%(info.resolution)s|%(info.title)s";

    /// <summary>Fichier où yt-dlp écrit le chemin de chaque fichier terminé, relu en fin de téléchargement.</summary>
    public static string FichierDeTrace => Path.Combine(DossierOutils, "dernier-telechargement.txt");

    /// <summary>Chemin de yt-dlp.exe, ou null s'il est introuvable.</summary>
    public static string? Trouver() => DansLesOutils(NomExecutable);

    /// <summary>
    /// ffmpeg assemble l'image et le son que les sites envoient séparément et convertit d'un format à l'autre.
    /// Deno exécute le code JavaScript dont YouTube a besoin pour donner ses adresses de vidéo.
    /// Sans eux l'interface marche encore, avec moins de choix.
    /// </summary>
    public static Complements TrouverComplements() => new(DansLesOutils("ffmpeg.exe"), DansLesOutils("deno.exe"));

    private static string? DansLesOutils(string nom)
    {
        var chemin = Path.Combine(DossierOutils, nom);
        return File.Exists(chemin) ? chemin : null;
    }

    /// <summary>
    /// Une adresse est acceptée seulement si c'est une adresse web complète en http ou https.
    /// Cela écarte les chemins de fichiers locaux et tout texte qui ressemblerait à une option.
    /// </summary>
    public static bool AdresseValide(string? texte, out string adresse)
    {
        // Un collage ramène parfois plusieurs lignes, seule la première compte.
        adresse = (texte ?? "").Trim().Split('\r', '\n')[0].Trim();
        return Uri.TryCreate(adresse, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Toutes les adresses web du texte, dans l'ordre et sans doublon. Plusieurs liens collés ensemble,
    /// séparés par des espaces ou des retours à la ligne, forment une file que l'application mène de front.
    /// </summary>
    public static List<string> AdressesValides(string? texte) =>
        (texte ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(morceau => AdresseValide(morceau, out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Arguments d'un téléchargement, un élément de liste par argument.
    /// Rien n'est recollé en une ligne de commande, l'adresse ne peut donc rien injecter.
    /// </summary>
    /// <param name="trace">
    /// Fichier où yt-dlp note le chemin du fichier terminé. Chaque téléchargement mené en parallèle a le sien,
    /// sinon ils écriraient tous au même endroit.
    /// </param>
    public static List<string> ArgumentsTelechargement(Demande demande, Complements complements, string? trace = null)
    {
        var arguments = new List<string>
        {
            "--ignore-config",          // comportement identique quel que soit un éventuel fichier de réglages de yt-dlp
            "--concurrent-fragments", "4",  // les vidéos servies en morceaux arrivent quatre morceaux à la fois
            "--newline",                // une ligne par mise à jour de progression, lisible par l'interface
            "--progress-template", GabaritDeMesure,
            "--color", "never",         // pas de codes de couleur dans le journal
            "--encoding", "utf-8",      // les accents des titres arrivent intacts
            "--no-mtime",               // le fichier porte la date du téléchargement, il se retrouve en tête du dossier
            // Une adresse qui désigne à la fois une vidéo et sa playlist ne donne que la vidéo.
            // Les playlists entières sont menées par l'application, une vidéo par lancement.
            "--no-playlist",
            "-P", demande.Dossier,
            "-o", ModeleDeNom(demande),
            // Les caractères interdits dans un nom de fichier deviennent des caractères simples (tiret, souligné),
            // et non des sosies exotiques. Le nom reste ainsi lisible sur Windows, Mac, Android et les clés USB.
            "--compat-options", "filename-sanitization",
            // Les mentions de vitrine, « (Official Video) », « [4K] », « (Paroles) », sont retirées du titre.
            // Elles ne disent rien du contenu et rendraient les noms irréguliers d'un fichier à l'autre.
            "--replace-in-metadata", "title,track", MentionsDeVitrine, "",
            // Le chemin final de chaque fichier, une fois converti et rangé, pour pouvoir le montrer à la fin.
            "--print-to-file", "after_move:filepath", trace ?? FichierDeTrace,
        };

        if (complements.Deno is not null)
            arguments.AddRange(["--js-runtimes", $"deno:{complements.Deno}"]);

        // Critères de tri des formats proposés par le site, du plus important au moins important.
        var tri = new List<string>();
        var hauteur = !demande.AudioSeul && demande.Qualite.Length > 0 ? $"res:{demande.Qualite}" : null;

        if (complements.Ffmpeg is null)
        {
            // Sans ffmpeg, on demande un fichier que le site fournit déjà complet.
            // « b » est la meilleure vidéo avec son déjà assemblée, « ba » la meilleure piste son telle quelle.
            if (hauteur is not null) tri.Add(hauteur);
            arguments.AddRange(["-f", demande.AudioSeul ? "ba/b" : "b"]);
        }
        else if (demande.AudioSeul)
        {
            arguments.AddRange(["--ffmpeg-location", complements.Ffmpeg, "--embed-metadata", "-f", "ba/b", "-x"]);
            arguments.AddRange(SortieAudio(demande.Format, demande.QualiteDuSon, demande.Egaliseur));
            // Pour M4A et AAC, on prend la piste AAC du site quand elle existe.
            // Partir de la piste Opus obligerait à la réencoder, avec une seconde perte de qualité.
            // Avec un égaliseur le son est réencodé quoi qu'il arrive, autant partir alors de la meilleure piste.
            if (demande.Format is "m4a" or "aac" && demande.Egaliseur.Length == 0) tri.Add("aext:m4a");
        }
        else
        {
            arguments.AddRange(["--ffmpeg-location", complements.Ffmpeg, "--embed-metadata", "-f", "bv*+ba/b"]);
            switch (demande.Format)
            {
                case "mp4":
                    // Le fichier qui se lit partout : téléphone, téléviseur, Windows, Mac. H.264 et AAC passent donc
                    // avant la hauteur d'image, quitte à s'arrêter à 1080p sur les sites qui réservent la 4K à d'autres codecs.
                    tri.Add("vcodec:h264");
                    if (hauteur is not null) tri.Add(hauteur);
                    tri.Add("aext:m4a");
                    arguments.AddRange(["--merge-output-format", "mp4", .. VersMp4]);
                    break;
                case "mp4max":
                    // La hauteur d'image d'abord. Au-delà de 1080p les sites n'ont souvent que du VP9 ou de l'AV1,
                    // le MP4 les contient alors tels quels et les appareils anciens ne les liront pas.
                    tri.AddRange([hauteur ?? "res", "vcodec:h264", "aext:m4a"]);
                    arguments.AddRange(["--merge-output-format", "mp4", .. VersMp4]);
                    break;
                case "mov":
                    // Le MOV ne tient bien que le H.264 et l'AAC, on les exige donc avant la hauteur d'image.
                    // Un fichier MP4 change simplement de boîte. Tout autre fichier est converti.
                    tri.Add("vcodec:h264");
                    if (hauteur is not null) tri.Add(hauteur);
                    tri.Add("aext:m4a");
                    arguments.AddRange(["--merge-output-format", "mov", "--remux-video", "mp4>mov/m4v>mov", "--recode-video", "mov"]);
                    break;
                case "mkv":
                    // Le MKV accepte tous les codecs, un simple changement de boîte suffit.
                    if (hauteur is not null) tri.Add(hauteur);
                    arguments.AddRange(["--merge-output-format", "mkv", "--remux-video", "mkv"]);
                    break;
                case "webm":
                    // On vise des pistes déjà en WebM pour éviter une conversion, qui n'a lieu que si le site n'en a pas.
                    tri.AddRange([hauteur ?? "res", "vext:webm", "aext:webm"]);
                    arguments.AddRange(["--recode-video", "webm"]);
                    break;
                case "flv":
                    // Laissé à ses réglages par défaut, ffmpeg encoderait le FLV avec un codec de 2002 à l'image très dégradée.
                    // On lui impose H.264 et AAC, que le FLV accepte.
                    if (hauteur is not null) tri.Add(hauteur);
                    arguments.AddRange(["--recode-video", "flv", "--postprocessor-args", "VideoConvertor:-c:v libx264 -c:a aac"]);
                    break;
                case "avi":
                    // yt-dlp choisit lui-même l'encodeur Xvid pour l'AVI.
                    if (hauteur is not null) tri.Add(hauteur);
                    arguments.AddRange(["--recode-video", "avi"]);
                    break;
                default:
                    if (hauteur is not null) tri.Add(hauteur);
                    break;
            }
        }

        // Jaquette. L'image de la vidéo est inscrite dans le fichier, convertie en JPEG que tous les lecteurs savent
        // afficher. Seulement pour les formats qui acceptent une image, yt-dlp échouerait sur les autres.
        var accepteUneJaquette = demande.AudioSeul
            ? demande.Format is "mp3" or "m4a" or "opus" or "vorbis" or "flac" or "alac"
            : demande.Format is "mp4" or "mp4max" or "mkv" or "mov";
        if (complements.Ffmpeg is not null && accepteUneJaquette)
            arguments.AddRange(["--embed-thumbnail", "--convert-thumbnails", "jpg"]);

        if (tri.Count > 0) arguments.AddRange(["-S", string.Join(',', tri)]);

        // Tout ce qui suit « -- » est lu comme une adresse, jamais comme une option.
        arguments.Add("--");
        arguments.Add(demande.Adresse);
        return arguments;
    }

    // Arrivée en MP4 d'un fichier qui ne l'est pas déjà. Les boîtes cousines du MP4 (MOV, M4V, FLV, TS) changent
    // simplement de boîte, c'est instantané. Les autres, le WebM surtout, sont converties en H.264 et AAC :
    // leurs codecs n'entrent pas proprement dans un MP4, et un simple changement de boîte échouerait.
    private static readonly string[] VersMp4 = ["--remux-video", "mov>mp4/m4v>mp4/flv>mp4/ts>mp4", "--recode-video", "mp4"];

    // Un groupe entre parenthèses ou crochets qui contient l'un de ces mots est une mention de vitrine.
    private const string MentionsDeVitrine =
        @"(?i)\s*[\(\[][^\)\]]*\b(official|officiel|lyrics?|paroles|audio|video|vidéo|clip|visualizer|hd|hq|4k)\b[^\)\]]*[\)\]]";

    /// <summary>
    /// Le nom donné aux fichiers, le même partout. « Titre » pour une vidéo. Pour la musique, « Artiste - Titre »
    /// quand le site connaît l'artiste et le morceau, sinon le titre seul (qui contient souvent déjà l'artiste).
    /// Une playlist va dans un dossier à son nom, chaque fichier précédé de son rang sur deux chiffres.
    /// </summary>
    private static string ModeleDeNom(Demande demande)
    {
        var nom = demande.AudioSeul ? "%(artist&{} - |)s%(track,title)s" : "%(title)s";
        return demande.PrefixeDeNom + nom + ".%(ext)s";
    }

    /// <summary>
    /// Le début du nom d'un fichier de playlist : le dossier de la playlist, puis le rang sur deux chiffres.
    /// Le texte est écrit en clair dans le modèle de yt-dlp, où le pour-cent annonce un champ, d'où son doublement.
    /// </summary>
    public static string PrefixeDeListe(string liste, int rang) =>
        (NomDeFichier(liste) + "/" + $"{rang:00} - ").Replace("%", "%%");

    /// <summary>
    /// Arguments pour retrouver et télécharger un morceau décrit par Spotify. yt-dlp cherche le titre sur
    /// YouTube Music (ou sur YouTube en second recours), ne garde qu'un résultat dont la durée colle à quelques
    /// secondes près, puis inscrit dans le fichier les informations de Spotify plutôt que celles de la vidéo.
    /// </summary>
    public static List<string> ArgumentsPourUnMorceau(
        Morceau morceau, string format, string dossier, bool dansUnSousDossier, Complements complements, bool surYoutubeMusic,
        string trace, string qualiteDuSon = "0", string egaliseur = "")
    {
        var arguments = new List<string>
        {
            "--ignore-config", "--newline", "--progress-template", GabaritDeMesure,
            "--color", "never", "--encoding", "utf-8", "--no-mtime",
            // Les cinq premiers résultats sont examinés, le premier dont la durée convient est pris, et on s'arrête là.
            "--playlist-end", "5", "--max-downloads", "1",
            "-P", dossier,
            "-o", NomDuMorceau(morceau, dansUnSousDossier) + ".%(ext)s",
            "--print-to-file", "after_move:filepath", trace,
            "--embed-metadata", "-f", "ba/b", "-x",
        };
        if (complements.Deno is not null) arguments.AddRange(["--js-runtimes", $"deno:{complements.Deno}"]);
        if (complements.Ffmpeg is not null) arguments.AddRange(["--ffmpeg-location", complements.Ffmpeg]);

        // La durée donnée par Spotify écarte les reprises, les versions longues et les concerts.
        if (morceau.Duree > 0)
            arguments.AddRange(["--match-filter", $"duration>={morceau.Duree - 6} & duration<={morceau.Duree + 6}"]);

        arguments.AddRange(SortieAudio(format, qualiteDuSon, egaliseur));
        if (format is "m4a" or "aac" && egaliseur.Length == 0) arguments.AddRange(["-S", "aext:m4a"]);
        if (format is "mp3" or "m4a" or "opus" or "vorbis" or "flac" or "alac")
        {
            // La jaquette d'un morceau est carrée mais YouTube la sert dans une image large. On la recadre au carré.
            arguments.AddRange(["--embed-thumbnail", "--convert-thumbnails", "jpg",
                "--ppa", "ThumbnailsConvertor+ffmpeg_o:-c:v mjpeg -vf crop=ih:ih"]);
        }

        // Informations de Spotify. Un champ « meta_ » s'impose à ce que yt-dlp aurait déduit de la vidéo.
        Etiqueter(arguments, "meta_title", morceau.Titre);
        Etiqueter(arguments, "meta_artist", morceau.Artiste);
        Etiqueter(arguments, "meta_album", morceau.Album);
        Etiqueter(arguments, "meta_album_artist", morceau.ArtisteDeLAlbum);
        Etiqueter(arguments, "meta_date", morceau.Annee);
        if (morceau.Piste > 0) Etiqueter(arguments, "meta_track", morceau.Piste.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var requete = $"{morceau.Artiste} {morceau.Titre}".Trim();
        arguments.Add("--");
        arguments.Add(surYoutubeMusic
            ? $"https://music.youtube.com/search?q={Uri.EscapeDataString(requete)}#songs"
            : $"ytsearch5:{requete}");
        return arguments;
    }

    /// <summary>
    /// Ajoute une étiquette à inscrire dans le fichier. yt-dlp attend « source:modèle ». La source est écrite
    /// en clair, précédée d'un repère, parce qu'un mot seul serait pris pour un nom de champ. Dans la source,
    /// le deux-points sépare et le pour-cent annonce un champ, on les neutralise donc.
    /// </summary>
    private static void Etiqueter(List<string> arguments, string champ, string valeur)
    {
        valeur = valeur.ReplaceLineEndings(" ").Replace('\\', '/').Trim();
        if (valeur.Length == 0) return;
        var source = valeur.Replace("%", "%%").Replace(":", "\\:");
        arguments.AddRange(["--parse-metadata", $"DE {source}:DE (?P<{champ}>.+)"]);
    }

    private static string NomDuMorceau(Morceau morceau, bool dansUnSousDossier) =>
        // Ce nom est écrit en clair dans le modèle de yt-dlp, où le pour-cent annonce un champ.
        NomDuMorceauEnClair(morceau, dansUnSousDossier).Replace("%", "%%");

    private static string NomDuMorceauEnClair(Morceau morceau, bool dansUnSousDossier)
    {
        var nom = NomDeFichier(morceau.Artiste.Length > 0 ? $"{morceau.Artiste} - {morceau.Titre}" : morceau.Titre);
        if (dansUnSousDossier && morceau.Liste.Length > 0)
        {
            var rang = morceau.Rang > 0 ? morceau.Rang.ToString("D" + Math.Max(2, morceau.LargeurDuRang)) + " - " : "";
            nom = NomDeFichier(morceau.Liste) + "/" + rang + nom;
        }
        return nom;
    }

    /// <summary>
    /// Le chemin du fichier qu'un morceau donnera, ou null quand son extension ne peut pas être connue d'avance
    /// (format d'origine). Il sert à sauter d'emblée un morceau déjà présent, pour reprendre une longue playlist.
    /// </summary>
    public static string? CheminAttenduDuMorceau(Morceau morceau, string format, string dossier, bool dansUnSousDossier)
    {
        var extension = format switch
        {
            "mp3" or "m4a" or "opus" or "flac" or "wav" or "aac" or "aiff" => format,
            "vorbis" => "ogg",
            "alac" => "m4a",
            _ => null,
        };
        return extension is null
            ? null
            : Path.Combine(dossier, NomDuMorceauEnClair(morceau, dansUnSousDossier).Replace('/', Path.DirectorySeparatorChar) + "." + extension);
    }

    // Le rang en tête d'un fichier de liste, « 007 - ».
    private static readonly System.Text.RegularExpressions.Regex RangEnTete = new(@"^\d{2,4} - ", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Le fichier de ce morceau s'il est déjà dans le dossier, sinon null. Dans une liste, il est reconnu même
    /// rangé sous un autre numéro : la playlist a pu changer d'ordre ou s'allonger depuis la fois précédente.
    /// </summary>
    public static string? MorceauDejaPresent(Morceau morceau, string format, string dossier, bool dansUnSousDossier)
    {
        if (CheminAttenduDuMorceau(morceau, format, dossier, dansUnSousDossier) is not { } attendu) return null;
        if (File.Exists(attendu)) return attendu;
        if (!dansUnSousDossier || Path.GetDirectoryName(attendu) is not { } repertoire || !Directory.Exists(repertoire)) return null;

        var nom = RangEnTete.Replace(Path.GetFileName(attendu), "");
        try
        {
            return Directory.EnumerateFiles(repertoire, "*" + Path.GetExtension(attendu))
                .FirstOrDefault(f => RangEnTete.Replace(Path.GetFileName(f), "").Equals(nom, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Débit du son converti. Zéro laisse l'encodeur viser sa meilleure qualité.
    public static readonly Choix[] QualitesDuSon =
    [
        new("La meilleure", "0"),
        new("320 kb/s", "320K"),
        new("256 kb/s", "256K"),
        new("192 kb/s", "192K"),
        new("128 kb/s", "128K"),
    ];

    /// <summary>Un texte rendu sûr comme nom de fichier sur Windows, Mac et Android, sans caractère interdit.</summary>
    private static string NomDeFichier(string texte)
    {
        var propre = new System.Text.StringBuilder(texte.Length);
        foreach (var c in texte)
        {
            propre.Append(c switch
            {
                '/' or '\\' or '|' => '-',
                ':' => '-',
                '"' => '\'',
                '?' or '*' or '<' or '>' => ' ',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }
        // Espaces en double réduits, puis ni point ni espace en fin de nom, que Windows refuse.
        var nom = string.Join(' ', propre.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (nom.Length > 140) nom = nom[..140].TrimEnd('.', ' ');
        return nom.Length > 0 ? nom : "Sans titre";
    }

    /// <summary>
    /// Lit le sommaire d'une playlist sans rien télécharger : son nom, puis l'adresse et le titre de chaque vidéo.
    /// Avec ce sommaire l'application mène elle-même la liste, plusieurs vidéos à la fois.
    /// Rend un nom vide quand l'adresse n'est pas une playlist (une vidéo seule, case cochée par erreur).
    /// </summary>
    public static async Task<(string Nom, List<(string Adresse, string Titre)> Videos)> ListerLaPlaylistAsync(
        string executable, string adresse, Complements complements)
    {
        // Une tabulation sépare les trois champs, aucun titre n'en contient.
        var arguments = new List<string>
        {
            "--ignore-config", "--no-warnings", "--encoding", "utf-8", "--yes-playlist", "--flat-playlist",
            "--print", "%(playlist_title,playlist_id|)s\t%(url,webpage_url|)s\t%(title|)s",
        };
        if (complements.Deno is not null) arguments.AddRange(["--js-runtimes", $"deno:{complements.Deno}"]);
        arguments.AddRange(["--", adresse]);

        var nom = "";
        var videos = new List<(string, string)>();
        foreach (var ligne in await InterrogerToutAsync(executable, arguments, secondes: 180))
        {
            var champs = ligne.Split('\t');
            if (champs.Length < 3 || champs[0].Length == 0 || champs[0] == "NA") continue;
            // Seules les adresses web sont gardées, comme pour une adresse collée à la main.
            if (!AdresseValide(champs[1], out var video)) continue;
            nom = champs[0];
            videos.Add((video, champs[2] == "NA" ? "" : champs[2]));
        }
        return (nom, videos);
    }

    /// <summary>Prépare un lancement de yt-dlp sans fenêtre noire, sorties redirigées en UTF-8.</summary>
    public static ProcessStartInfo Preparer(string executable, IEnumerable<string> arguments)
    {
        var demarrage = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,    // lancement direct du programme, aucun interpréteur de commandes entre les deux
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DossierOutils,
        };
        foreach (var argument in arguments) demarrage.ArgumentList.Add(argument);
        return demarrage;
    }

    /// <summary>
    /// Numéro de version de yt-dlp, ou null s'il ne répond pas. Le délai est large, la toute première
    /// exécution après le déploiement est ralentie par l'antivirus qui examine le fichier.
    /// </summary>
    public static async Task<string?> LireVersionAsync(string executable)
    {
        var lignes = await InterrogerToutAsync(executable, ["--version"], secondes: 45, exigerLaReussite: true);
        return lignes.Count > 0 ? lignes[0] : null;
    }

    /// <summary>
    /// Pose une question à yt-dlp et rend les lignes de sa réponse, sans les lignes vides. La liste est vide
    /// s'il ne répond pas ou tarde trop. Pour un sommaire de playlist on garde ce qu'il a pu lire même quand
    /// une vidéo retirée lui fait rendre un code d'erreur.
    /// </summary>
    private static async Task<List<string>> InterrogerToutAsync(
        string executable, IEnumerable<string> arguments, int secondes, bool exigerLaReussite = false)
    {
        var reponse = await InterrogerAsync(executable, arguments, secondes, exigerLaReussite);
        return reponse is null
            ? []
            : reponse.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>La réponse brute de yt-dlp, ou null s'il échoue ou tarde trop.</summary>
    private static async Task<string?> InterrogerAsync(string executable, IEnumerable<string> arguments, int secondes, bool exigerLaReussite)
    {
        try
        {
            using var processus = Process.Start(Preparer(executable, arguments));
            if (processus is null) return null;

            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(secondes));
            try
            {
                // Le canal d'erreur doit rester branché et être vidé. Débranché, yt-dlp refuse de démarrer
                // depuis une application sans console. Laissé plein, il pourrait le bloquer.
                var erreurs = processus.StandardError.ReadToEndAsync(delai.Token);
                var sortie = await processus.StandardOutput.ReadToEndAsync(delai.Token);
                await erreurs;
                await processus.WaitForExitAsync(delai.Token);
                var reponse = sortie.ReplaceLineEndings("\n").Trim();
                if (exigerLaReussite && processus.ExitCode != 0) return null;
                return reponse.Length > 0 ? reponse : null;
            }
            catch (OperationCanceledException)
            {
                ArbreDeProcessus.Arreter(processus);
                return null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Les fichiers que le dernier téléchargement a produits, dans l'ordre où ils ont été terminés.</summary>
    public static List<string> LireLaTrace(string? trace = null)
    {
        trace ??= FichierDeTrace;
        try
        {
            return File.Exists(trace)
                ? File.ReadAllLines(trace, Encoding.UTF8).Select(l => l.Trim()).Where(File.Exists).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void EffacerLaTrace(string? trace = null)
    {
        try
        {
            File.Delete(trace ?? FichierDeTrace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Au pire la fin du téléchargement montrera aussi les fichiers de la fois précédente.
        }
    }
}

/// <summary>
/// Les réglages gardés d'une fois sur l'autre : le dossier de destination, le nombre de téléchargements
/// à la fois, la qualité du son. Un petit fichier texte, une ligne « clé=valeur » par réglage.
/// </summary>
internal static class Reglages
{
    private static string Fichier => Path.Combine(YtDlp.DossierOutils, "reglages.txt");

    private static Dictionary<string, string> Tout()
    {
        var reglages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Fichier)) return reglages;
            foreach (var ligne in File.ReadAllLines(Fichier))
            {
                var coupe = ligne.IndexOf('=');
                // Les premières versions n'écrivaient que le dossier, seul sur sa ligne. On le relit tel quel.
                if (coupe <= 0 || ligne.Contains(":\\", StringComparison.Ordinal) && !ligne.StartsWith("dossier=", StringComparison.OrdinalIgnoreCase))
                {
                    if (ligne.Trim().Length > 0) reglages.TryAdd("dossier", ligne.Trim());
                    continue;
                }
                reglages[ligne[..coupe].Trim()] = ligne[(coupe + 1)..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Réglages illisibles, on repart des valeurs par défaut.
        }
        return reglages;
    }

    public static string Lire(string cle, string defaut) => Tout().GetValueOrDefault(cle) is { Length: > 0 } valeur ? valeur : defaut;

    public static void Ecrire(string cle, string valeur)
    {
        try
        {
            var reglages = Tout();
            reglages[cle] = valeur;
            Directory.CreateDirectory(YtDlp.DossierOutils);
            File.WriteAllLines(Fichier, reglages.Select(r => $"{r.Key}={r.Value}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ne pas retenir un réglage n'empêche pas de télécharger.
        }
    }

    public static string LireDossier()
    {
        var dossier = Lire("dossier", "");
        if (dossier.Length > 0 && Directory.Exists(dossier)) return dossier;

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Directory.Exists(videos)
            ? videos
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static void EcrireDossier(string dossier) => Ecrire("dossier", dossier);
}
