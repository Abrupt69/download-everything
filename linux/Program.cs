using Avalonia;

namespace YtDlpInterface;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Mode d'essai sans fenêtre, pour vérifier le moteur là où il n'y a pas d'écran (voir EssaiSansEcran).
        if (args is ["--essai", ..]) return EssaiSansEcran.LancerAsync(args).GetAwaiter().GetResult();
        // Tout ce qui n'est pas une option est une adresse à poser dans le champ : un lien magnet, un fichier .torrent.
        FenetrePrincipale.AdressesDeDepart = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia cherche cette méthode par son nom pour l'aperçu des dessins, d'où son nom en anglais.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}