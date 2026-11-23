using System.Globalization;
using System.IO;
using System.Text;

namespace YtDlpInterface;

/// <summary>
/// L'historique des téléchargements, ajouté le 23/11/2026. Une ligne par fichier reçu, dans
/// « historique.txt », à côté des réglages. Il reste sur le poste, rien n'est envoyé nulle part, et un bouton
/// l'efface. Un fichier déjà présent dans le dossier n'y entre pas : il n'a pas été téléchargé cette fois-ci.
///
/// Une ligne, ce sont six champs séparés par une tabulation : la date (heure locale), le genre (son, vidéo,
/// image, fichier, torrent), le titre, l'adresse d'origine, le chemin du fichier et sa taille en octets.
/// Les tabulations et retours à la ligne d'un titre sont remplacés par des espaces, pour que la ligne tienne.
/// </summary>
internal static class Historique
{
    /// <summary>Un téléchargement de l'historique.</summary>
    internal sealed record Entree(DateTime Quand, string Genre, string Titre, string Source, string Chemin, long Taille);

    // Au-delà, les plus anciennes lignes sont oubliées. De quoi garder des mois de playlists sans alourdir l'ouverture.
    private const int Plafond = 5000;

    private static readonly object Verrou = new();

    private static string Fichier => Path.Combine(YtDlp.DossierOutils, "historique.txt");

    /// <summary>Note un fichier qui vient d'arriver. Une erreur d'écriture n'empêche jamais un téléchargement.</summary>
    public static void Noter(string genre, string source, string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            var titre = Path.GetFileNameWithoutExtension(chemin);
            var ligne = string.Join('\t',
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture), Propre(genre), Propre(titre), Propre(source), Propre(chemin),
                (info.Exists ? info.Length : 0).ToString(CultureInfo.InvariantCulture));
            lock (Verrou)
            {
                Directory.CreateDirectory(YtDlp.DossierOutils);
                File.AppendAllText(Fichier, ligne + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // L'historique est un confort. Le téléchargement, lui, a réussi.
        }
    }

    /// <summary>Tout l'historique, le plus récent en premier. Les lignes illisibles sont ignorées.</summary>
    public static List<Entree> Lire()
    {
        var entrees = new List<Entree>();
        string[] lignes;
        try
        {
            lock (Verrou)
            {
                if (!File.Exists(Fichier)) return entrees;
                lignes = File.ReadAllLines(Fichier, Encoding.UTF8);
                // Le fichier a dépassé le plafond : on le réécrit sans les plus anciennes lignes.
                if (lignes.Length > Plafond)
                {
                    lignes = lignes[^Plafond..];
                    File.WriteAllLines(Fichier, lignes, new UTF8Encoding(false));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return entrees;
        }

        foreach (var ligne in lignes)
        {
            if (ligne.Split('\t') is not [var quand, var genre, var titre, var source, var chemin, var taille]) continue;
            if (!DateTime.TryParse(quand, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            entrees.Add(new Entree(date, genre, titre, source, chemin, long.TryParse(taille, out var octets) ? octets : 0));
        }
        entrees.Reverse();
        return entrees;
    }

    /// <summary>Efface tout l'historique. Les fichiers téléchargés, eux, ne sont pas touchés.</summary>
    public static void Effacer()
    {
        lock (Verrou)
        {
            if (File.Exists(Fichier)) File.Delete(Fichier);
        }
    }

    /// <summary>Le nom court du site d'une adresse, « youtube.com » pour « https://www.youtube.com/watch?v=… ».</summary>
    public static string Site(string adresse)
    {
        if (adresse.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return "lien magnet";
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "";
        var hote = uri.Host;
        foreach (var debut in new[] { "www.", "m.", "music.", "open." })
            if (hote.StartsWith(debut, StringComparison.OrdinalIgnoreCase)) return hote[debut.Length..];
        return hote;
    }

    private static string Propre(string texte) => texte.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>Une ligne de la vue de l'historique. Elle ne dépend d'aucune bibliothèque d'interface.</summary>
internal sealed class LigneDHistorique(Historique.Entree entree) : Observable
{
    public Historique.Entree Entree { get; } = entree;
    public string Date { get; } = entree.Quand.ToString("dd/MM HH'h'mm", CultureInfo.GetCultureInfo("fr-FR"));
    public string Titre => Entree.Titre;
    public string Detail { get; } = string.Join("  ·  ", new[]
    {
        entree.Genre,
        Historique.Site(entree.Source),
        entree.Taille > 0 ? FenetrePrincipale.Octets(entree.Taille) : "",
    }.Where(morceau => morceau.Length > 0));

    public string Chemin => Entree.Chemin;

    /// <summary>Le fichier n'est plus là où il a été rangé : la ligne s'éteint et le bouton Dossier ouvre le dossier seul.</summary>
    public bool Absent { get; } = !File.Exists(entree.Chemin) && !Directory.Exists(entree.Chemin);

    /// <summary>L'opacité de la ligne, éteinte quand le fichier n'est plus là. Une simple valeur, que WPF et Avalonia lisent pareil.</summary>
    public double Opacite => Absent ? 0.45 : 1.0;

    /// <summary>Pour la recherche : le titre, le site et le genre, sans casse.</summary>
    public bool Correspond(string recherche) =>
        recherche.Length == 0
        || Entree.Titre.Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
        || Entree.Source.Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
        || Entree.Genre.Contains(recherche, StringComparison.CurrentCultureIgnoreCase);
}
