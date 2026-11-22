using System.Diagnostics;
using System.IO;
using System.Text;

namespace YtDlpInterface;

/// <summary>
/// gallery-dl, le second recours de l'application. yt-dlp ne prend que la vidéo et le son. Quand il ne trouve
/// rien à une adresse (une galerie d'images, une photo seule), gallery-dl est essayé à sa place, sans rien demander.
/// </summary>
internal static class GalleryDl
{
#if PORTABLE
    // Sous Linux l'exécutable publié par l'auteur s'appelle « gallery-dl.bin ».
    public static string NomExecutable => Plateforme.Windows ? "gallery-dl.exe" : "gallery-dl.bin";
#else
    public const string NomExecutable = "gallery-dl.exe";
#endif

    public static string? Trouver()
    {
        var chemin = Path.Combine(YtDlp.DossierOutils, NomExecutable);
        return File.Exists(chemin) ? chemin : null;
    }

    /// <summary>
    /// Faux pour les adresses où gallery-dl n'apportera rien. Un échec sur YouTube vient d'une vidéo privée,
    /// retirée ou refusée, jamais d'une galerie d'images, inutile de le faire attendre pour rien.
    /// </summary>
    public static bool PeutAider(string adresse)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return false;
        var hote = uri.Host.ToLowerInvariant();
        return !(hote.EndsWith("youtube.com", StringComparison.Ordinal) || hote.EndsWith("youtu.be", StringComparison.Ordinal)
                 || hote.EndsWith("youtube-nocookie.com", StringComparison.Ordinal));
    }

    /// <summary>
    /// Les arguments d'une récupération. Les images sont rangées dans le dossier choisi, sous un dossier au nom
    /// du site puis de la galerie, le rangement habituel de gallery-dl. Aucun fichier de réglages du poste n'est lu,
    /// pour que l'outil se comporte de la même façon chez tout le monde.
    /// </summary>
    public static List<string> Arguments(string adresse, string dossier) =>
    [
        "--config-ignore",
        // Les noms de fichiers arrivent en UTF-8 sur les deux canaux, accents compris.
        "-o", "output.stdout=utf-8",
        "-o", "output.stderr=utf-8",
        "--no-colors",
        "-d", dossier,
        "--", adresse,
    ];

    /// <summary>Prépare un lancement sans fenêtre noire, sorties redirigées en UTF-8, comme pour yt-dlp.</summary>
    public static ProcessStartInfo Preparer(string executable, IEnumerable<string> arguments)
    {
        var demarrage = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = YtDlp.DossierOutils,
        };
        foreach (var argument in arguments) demarrage.ArgumentList.Add(argument);
        return demarrage;
    }

    /// <summary>
    /// gallery-dl écrit le chemin de chaque fichier reçu, seul sur sa ligne, et le fait précéder de « # »
    /// quand le fichier était déjà là. Rend ce chemin s'il désigne bien un fichier, sinon null (ligne de journal).
    /// </summary>
    public static (string? Fichier, bool DejaLa) LireUneLigne(string ligne)
    {
        var dejaLa = ligne.StartsWith("# ", StringComparison.Ordinal);
        var chemin = (dejaLa ? ligne[2..] : ligne).Trim();
        try
        {
            return chemin.Length > 3 && Path.IsPathFullyQualified(chemin) && File.Exists(chemin) ? (chemin, dejaLa) : (null, false);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return (null, false);
        }
    }

    /// <summary>Numéro de version, ou null si l'outil ne répond pas.</summary>
    public static async Task<string?> LireVersionAsync(string executable)
    {
        try
        {
            using var processus = Process.Start(Preparer(executable, ["--version"]));
            if (processus is null) return null;
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var erreurs = processus.StandardError.ReadToEndAsync(delai.Token);
                var sortie = await processus.StandardOutput.ReadToEndAsync(delai.Token);
                await erreurs;
                await processus.WaitForExitAsync(delai.Token);
                return processus.ExitCode == 0 && sortie.Trim() is { Length: > 0 } version ? version : null;
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
}
