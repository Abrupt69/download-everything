using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace YtDlpInterface;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime bureau)
        {
            var fenetre = new FenetrePrincipale();
            bureau.MainWindow = fenetre;
            // Une erreur imprévue est dite à l'utilisateur au lieu de fermer la fenêtre sans un mot,
            // et son détail est gardé dans le dossier des outils pour pouvoir la comprendre après coup.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Noter(e.Exception);
                e.Handled = true;
                var quoi = e.Exception.Message.Trim() is { Length: > 0 } message ? message : e.Exception.GetType().Name;
                try { fenetre.DireUneErreurImprevue(quoi); }
                catch (Exception) { /* la fenêtre elle-même est en cause, le fichier garde la trace */ }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

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