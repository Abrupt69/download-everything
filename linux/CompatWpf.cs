using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace YtDlpInterface;

// Le code de la fenêtre est commun à la version Windows (WPF) et à la version Linux (Avalonia). Les deux
// bibliothèques se ressemblent beaucoup mais nomment quelques choses autrement. Plutôt que de dédoubler le code,
// ce fichier donne à Avalonia les quelques noms de WPF dont la fenêtre se sert. Il n'existe que côté Linux.

/// <summary>WPF dit « Visibility », Avalonia dit « IsVisible ». Voir l'extension ci-dessous.</summary>
internal enum Visibility
{
    Visible,
    Hidden,
    Collapsed,
}

/// <summary>L'état de la barre des tâches de Windows. Linux n'en a pas, la valeur est simplement gardée.</summary>
internal enum TaskbarItemProgressState
{
    None,
    Indeterminate,
    Normal,
    Error,
    Paused,
}

/// <summary>Tient lieu de la progression sur l'icône de la barre des tâches, qui n'existe que sous Windows.</summary>
internal sealed class BarreDesTachesFactice
{
    public TaskbarItemProgressState ProgressState { get; set; }

    public double ProgressValue { get; set; }
}

internal static class FontWeights
{
    public static FontWeight Normal => FontWeight.Normal;

    public static FontWeight SemiBold => FontWeight.SemiBold;
}

internal static class FontStretches
{
    public static FontStretch Normal => FontStretch.Normal;

    public static FontStretch SemiCondensed => FontStretch.SemiCondensed;
}

internal static class CompatWpf
{
    extension(Visual visuel)
    {
        /// <summary>Montre ou cache un élément, à la façon de WPF.</summary>
        public Visibility Visibility
        {
            get => visuel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            set => visuel.IsVisible = value == Visibility.Visible;
        }
    }

    extension(Control controle)
    {
        /// <summary>L'infobulle d'un élément, à la façon de WPF.</summary>
        public object? ToolTip
        {
            get => Avalonia.Controls.ToolTip.GetTip(controle);
            set => Avalonia.Controls.ToolTip.SetTip(controle, value);
        }
    }

    /// <summary>
    /// Donne à une propriété la valeur d'une ressource de la fenêtre, une couleur de la palette le plus souvent.
    /// La palette ne change pas en cours de route, la valeur est donc simplement recopiée.
    /// </summary>
    public static void SetResourceReference(this Control controle, AvaloniaProperty propriete, string cle)
    {
        if (controle.TryFindResource(cle, controle.ActualThemeVariant, out var valeur)) controle.SetValue(propriete, valeur);
    }

    /// <summary>Confie une action au fil de la fenêtre, depuis n'importe quel autre fil.</summary>
    public static void BeginInvoke(this Dispatcher fil, Action action) => fil.Post(action);

    public static void BeginInvoke(this Dispatcher fil, DispatcherPriority priorite, Action action) => fil.Post(action, priorite);

    /// <summary>Laisse passer ce qui attend déjà sur le fil de la fenêtre avant de continuer.</summary>
    public static async Task Yield(this Dispatcher fil, DispatcherPriority priorite) => await fil.InvokeAsync(() => { }, priorite);
}
