using System.IO;
using System.Windows;

namespace YtDlpInterface;

public partial class App : Application
{
    public App()
    {
        // Une erreur imprévue est dite à l'utilisateur au lieu de fermer la fenêtre sans un mot.
        DispatcherUnhandledException += (_, e) =>
        {
            // Certaines erreurs n'ont pas de message. Leur nom vaut mieux qu'une boîte vide.
            var quoi = e.Exception.Message.Trim() is { Length: > 0 } message ? message : e.Exception.GetType().Name;
            Noter(e.Exception);
            MessageBox.Show(
                $"Une erreur inattendue s'est produite.\n\n{quoi}\n\nVous pouvez continuer ou relancer l'application.",
                "Download Everything", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
    }

    /// <summary>Garde le détail de la dernière erreur imprévue dans le dossier des outils, pour pouvoir la comprendre après coup.</summary>
    private static void Noter(Exception erreur)
    {
        try
        {
            Directory.CreateDirectory(YtDlp.DossierOutils);
            File.WriteAllText(Path.Combine(YtDlp.DossierOutils, "derniere-erreur.txt"),
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}, version {VersionDeLApplication.Lisible(VersionDeLApplication.Actuelle)}\n{erreur}\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ne pas pouvoir noter l'erreur ne doit pas en créer une seconde.
        }
    }
}