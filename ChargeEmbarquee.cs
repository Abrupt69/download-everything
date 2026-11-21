using System.IO;
using System.Reflection;

namespace YtDlpInterface;

/// <summary>
/// Les programmes que l'exécutable transporte avec lui (yt-dlp, ffmpeg, ffprobe, Deno), pour qu'un seul fichier
/// suffise sur un PC où rien n'est installé. À la première ouverture ils sont déposés dans le dossier des outils.
/// </summary>
internal static class ChargeEmbarquee
{
    // Les programmes sont rangés dans un assemblage à part. Il pèse plusieurs centaines de Mo et n'est
    // chargé en mémoire que le jour où il faut déployer, jamais aux ouvertures suivantes.
    private const string NomAssemblage = "YtDlpInterface.Charge";

    // À augmenter quand un programme est ajouté à la charge. Un PC déjà équipé d'une charge plus ancienne
    // reçoit alors les programmes qui lui manquent, sans que les siens soient remplacés.
    private const int Generation = 2;

    // Écrit en dernier. Sa présence dit que le déploiement est allé au bout, même si la fenêtre
    // a été fermée ou le PC éteint pendant une tentative précédente.
    private static string Temoin => Path.Combine(YtDlp.DossierOutils, $"outils-complets-{Generation}.txt");

    public static bool EstDeployee() =>
        File.Exists(Temoin) && YtDlp.Trouver() is not null;

    /// <summary>
    /// Dépose dans le dossier des outils chaque fichier embarqué qui n'y est pas encore.
    /// Un fichier déjà présent n'est jamais remplacé, pour ne pas revenir en arrière sur un yt-dlp
    /// que l'utilisateur a mis à jour depuis. Rend faux quand l'exécutable ne transporte rien.
    /// </summary>
    public static bool Deployer(Action<int, int>? avancement = null)
    {
        Assembly charge;
        try
        {
            charge = Assembly.Load(new AssemblyName(NomAssemblage));
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }

        Directory.CreateDirectory(YtDlp.DossierOutils);

        // Les bibliothèques d'abord, les programmes ensuite. Un programme n'apparaît ainsi qu'une fois
        // tout ce dont il dépend déjà en place.
        var noms = charge.GetManifestResourceNames()
            .OrderBy(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < noms.Length; i++)
        {
            avancement?.Invoke(i + 1, noms.Length);
            // Le nom vient de notre propre assemblage, mais on n'en garde que la partie fichier par principe.
            var cible = Path.Combine(YtDlp.DossierOutils, Path.GetFileName(noms[i]));
            if (File.Exists(cible))
            {
                // Le relevé des versions existe déjà. On y ajoute seulement les outils qu'il ne connaît pas encore,
                // sans toucher aux versions que l'utilisateur a mises à jour depuis.
                if (noms[i].Equals("versions.txt", StringComparison.OrdinalIgnoreCase))
                    CompleterLesVersions(charge.GetManifestResourceStream(noms[i])!, cible);
                continue;
            }

            // Écriture sous un nom provisoire puis renommage, pour ne jamais laisser un programme à moitié écrit.
            var provisoire = cible + ".partiel";
            using (var source = charge.GetManifestResourceStream(noms[i])!)
            using (var sortie = new FileStream(provisoire, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                source.CopyTo(sortie);
            File.Move(provisoire, cible, overwrite: true);
        }

        File.WriteAllText(Temoin, $"Outils déployés le {DateTime.Now:dd/MM/yyyy} par Download Everything.");
        return noms.Length > 0;
    }

    private static void CompleterLesVersions(Stream embarquees, string fichier)
    {
        using var lecture = new StreamReader(embarquees);
        var connues = File.ReadAllLines(fichier).Select(l => l.Split('=')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nouvelles = lecture.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains('=') && !connues.Contains(l.Split('=')[0].Trim()))
            .ToList();
        if (nouvelles.Count > 0) File.AppendAllLines(fichier, nouvelles);
    }
}
