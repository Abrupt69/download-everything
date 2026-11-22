using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YtDlpInterface;

/// <summary>Un morceau tel que Spotify le décrit. C'est ce qui sert à le retrouver ailleurs, puis à étiqueter le fichier.</summary>
/// <param name="LargeurDuRang">
/// Nombre de chiffres du rang dans le nom du fichier. Deux d'ordinaire, trois pour une playlist de plus de
/// cent titres, pour que « 099 » se range bien avant « 100 ».
/// </param>
internal sealed record Morceau(
    string Titre, string Artiste, string Album, string ArtisteDeLAlbum, string Annee,
    int Piste, int Duree, string Liste, int Rang, int LargeurDuRang = 2);

/// <summary>
/// spotDL sert de recours pour lire un lien Spotify (morceau, album, playlist, artiste) et dire quels morceaux
/// il contient, quand la page publique de Spotify ne suffit pas (voir PageSpotify). Il est lent, plusieurs
/// minutes pour un album. Le téléchargement lui-même est confié à yt-dlp, bien plus rapide et régulier que le
/// moteur interne de spotDL, qui échouait une fois sur deux aux essais. Les flux de Spotify, protégés, ne sont pas lus.
/// </summary>
internal static class SpotDl
{
    /// <summary>Chemin de spotdl.exe dans le dossier des outils, ou null s'il manque.</summary>
    public static string? Trouver()
    {
#if PORTABLE
        var chemin = Path.Combine(YtDlp.DossierOutils, Plateforme.Outil("spotdl"));
#else
        var chemin = Path.Combine(YtDlp.DossierOutils, "spotdl.exe");
#endif
        return File.Exists(chemin) ? chemin : null;
    }

    public static bool EstUneAdresseSpotify(string adresse) =>
        Uri.TryCreate(adresse, UriKind.Absolute, out var uri)
        && uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fichier où spotDL écrit la description des morceaux, relu juste après.</summary>
    public static string FichierDesMorceaux => Path.Combine(YtDlp.DossierOutils, "morceaux-spotify.spotdl");

    /// <summary>La commande « save » décrit les morceaux d'un lien sans rien télécharger.</summary>
    public static List<string> ArgumentsDeLecture(string adresse, string ffmpeg) =>
    [
        "save", adresse,
        "--save-file", FichierDesMorceaux,
        "--ffmpeg", ffmpeg,             // spotDL refuse de démarrer sans, même pour une simple lecture
        "--simple-tui",                 // des lignes simples, pas un écran animé
        "--log-level", "INFO",
    ];

    public static ProcessStartInfo Preparer(string executable, IEnumerable<string> arguments)
    {
        var demarrage = YtDlp.Preparer(executable, arguments);
        demarrage.Environment["PYTHONIOENCODING"] = "utf-8";
        // Sans console, spotDL coupe ses lignes à 80 caractères. Une grande largeur les garde entières.
        demarrage.Environment["COLUMNS"] = "400";
        return demarrage;
    }

    /// <summary>Les morceaux décrits par la dernière lecture, dans l'ordre de l'album ou de la playlist.</summary>
    public static List<Morceau> LireLesMorceaux()
    {
        var morceaux = new List<Morceau>();
        try
        {
            if (!File.Exists(FichierDesMorceaux)) return morceaux;
            using var document = JsonDocument.Parse(File.ReadAllText(FichierDesMorceaux));
            foreach (var m in document.RootElement.EnumerateArray())
            {
                var titre = Texte(m, "name");
                if (titre.Length == 0) continue;
                morceaux.Add(new Morceau(
                    titre,
                    Texte(m, "artist"),
                    Texte(m, "album_name"),
                    Texte(m, "album_artist"),
                    Texte(m, "year"),
                    Entier(m, "track_number"),
                    Entier(m, "duration"),
                    Texte(m, "list_name"),
                    Entier(m, "list_position")));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Fichier absent ou illisible : la liste reste vide et la fenêtre le dira.
        }
        return morceaux;
    }

    public static void EffacerLesMorceaux()
    {
        try
        {
            File.Delete(FichierDesMorceaux);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Au pire la lecture suivante le réécrira.
        }
    }

    // Selon le champ, spotDL écrit du texte, un nombre ou rien du tout. Ces deux lectures acceptent les trois.
    private static string Texte(JsonElement morceau, string champ) =>
        morceau.TryGetProperty(champ, out var valeur)
            ? valeur.ValueKind switch
            {
                JsonValueKind.String => (valeur.GetString() ?? "").Trim(),
                JsonValueKind.Number => valeur.GetRawText(),
                _ => "",
            }
            : "";

    private static int Entier(JsonElement morceau, string champ) =>
        double.TryParse(Texte(morceau, champ), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var valeur) ? (int)Math.Round(valeur) : 0;
}
