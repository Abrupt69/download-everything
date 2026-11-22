using System.Diagnostics;

namespace YtDlpInterface;

/// <summary>
/// Ce qui change d'un système à l'autre, rassemblé ici pour la version portable : le nom des exécutables,
/// où trouver les outils du système, comment rendre un fichier exécutable, comment ouvrir un dossier.
/// </summary>
internal static class Plateforme
{
    public static bool Windows => OperatingSystem.IsWindows();

    /// <summary>Le nom de fichier d'un outil du dossier des outils : « .exe » sous Windows, rien ailleurs.</summary>
    public static string Outil(string nom) => Windows ? nom + ".exe" : nom;

    /// <summary>
    /// Cherche un programme installé sur le système (ffmpeg, aria2c), dans les dossiers du PATH.
    /// Sous Linux ces deux-là viennent des paquets du système, tenus à jour avec lui.
    /// </summary>
    public static string? DansLeSysteme(string nom)
    {
        foreach (var dossier in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var chemin = Path.Combine(dossier, Outil(nom));
                if (File.Exists(chemin)) return chemin;
            }
            catch (ArgumentException)
            {
                // Une entrée du PATH mal formée ne doit pas empêcher de lire les suivantes.
            }
        }
        return null;
    }

    /// <summary>Donne à un fichier téléchargé le droit d'être lancé. Sans effet sous Windows.</summary>
    public static void RendreExecutable(string chemin)
    {
        if (Windows) return;
        File.SetUnixFileMode(chemin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Rend un fichier lisible et modifiable par son seul propriétaire. Sans effet sous Windows.</summary>
    public static void ReserverAuProprietaire(string chemin)
    {
        if (Windows) return;
        try { File.SetUnixFileMode(chemin, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Ouvre un dossier, un fichier ou une adresse web avec le programme que le système lui associe.
    /// Sous Linux c'est le rôle de xdg-open, que .NET appelle de lui-même.
    /// </summary>
    public static void Ouvrir(string cible) => Process.Start(new ProcessStartInfo(cible) { UseShellExecute = true })?.Dispose();
}