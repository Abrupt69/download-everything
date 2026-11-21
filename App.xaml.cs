using System.Windows;

namespace YtDlpInterface;

public partial class App : Application
{
    public App()
    {
        // Une erreur imprévue est dite à l'utilisateur au lieu de fermer la fenêtre sans un mot.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(
                $"Une erreur inattendue s'est produite.\n\n{e.Exception.Message}\n\nVous pouvez continuer ou relancer l'application.",
                "Télécharger avec yt-dlp", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
    }
}
