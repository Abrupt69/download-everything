using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
#if PORTABLE
// Version Linux : la même fenêtre, dessinée avec Avalonia (voir le dossier linux). Les quelques noms de WPF
// dont ce fichier se sert et qu'Avalonia appelle autrement sont fournis par linux/CompatWpf.cs.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
#endif

namespace YtDlpInterface;

/// <summary>Une vidéo d'une playlist, telle que la liste dépliable la montre.</summary>
internal sealed class ElementDePlaylist(int numero) : INotifyPropertyChanged
{
    private string _titre = $"Vidéo {numero}";
    private double _pourcent;
    private string _etat = "en attente";

    public int Numero { get; } = numero;
    public string Titre { get => _titre; set => Changer(ref _titre, value, nameof(Titre)); }
    public double Pourcent { get => _pourcent; set => Changer(ref _pourcent, value, nameof(Pourcent)); }
#if PORTABLE
    public string Etat
    {
        get => _etat;
        set
        {
            Changer(ref _etat, value, nameof(Etat));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fini)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnEchec)));
        }
    }

    // Avalonia n'a pas les déclencheurs de WPF : la couleur de la barre de la ligne suit ces deux valeurs.
    public bool Fini => _etat is "terminé" or "déjà là";

    public bool EnEchec => _etat is "erreur" or "refusé" or "introuvable";
#else
    public string Etat { get => _etat; set => Changer(ref _etat, value, nameof(Etat)); }
#endif

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changer<T>(ref T champ, T valeur, string nom)
    {
        if (EqualityComparer<T>.Default.Equals(champ, valeur)) return;
        champ = valeur;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nom));
    }
}

/// <summary>
/// La fenêtre unique de l'application. Elle recueille la demande, lance yt-dlp
/// et recopie ses mesures et son journal au fil de l'eau.
/// </summary>
public partial class FenetrePrincipale : Window
{
    private enum Ton { Neutre, Reussite, Erreur }

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    private Process? _enCours;
    private bool _occupe;                       // un travail est en cours, du premier clic jusqu'au dernier message
    private bool _miseAJour;                    // ce travail est une mise à jour, la fenêtre ne se ferme pas au milieu
    private bool _annulationDemandee;
    private bool _dejaPresent;                  // yt-dlp a trouvé le fichier déjà dans le dossier et ne l'a pas repris
    private bool _imageEcartee;                 // yt-dlp a trouvé une image et l'a laissée, c'est à gallery-dl de la prendre
    private CancellationTokenSource? _arretMiseAJour;
    private string _dossier = "";
    private string? _dernierFichier;
    private Complements _complements = new(null, null);

    // Playlist en cours. _total reste à zéro pour une vidéo seule.
    private readonly ObservableCollection<ElementDePlaylist> _elements = [];
    private int _courant;
    private int _total;
    // Vrai quand l'application tient elle-même la liste (morceaux d'un lien Spotify, un lancement de yt-dlp chacun).
    // Les annonces de playlist de yt-dlp, qui ne parlent alors que de ses résultats de recherche, sont ignorées.
    private bool _listeMaitrisee;

    // yt-dlp annonce chaque vidéo d'une playlist par « [download] Downloading item 3 of 12 ».
    [GeneratedRegex(@"^\[download\] Downloading item (\d+) of (\d+)")]
    private static partial Regex AnnonceDElement();

    // Étapes où ffmpeg travaille sur le fichier reçu. Elles n'annoncent aucun pourcentage.
    private static readonly string[] EtapesDeFfmpeg =
        ["[Merger]", "[VideoRemuxer]", "[VideoConvertor]", "[ExtractAudio]", "[Metadata]", "[Fixup"];

    // Seuls ces types de fichiers sont ouverts d'un clic. Tout autre fichier est seulement montré dans son dossier.
    private static readonly HashSet<string> ExtensionsDeMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".flv", ".m4v", ".3gp", ".ts",
        ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".flac", ".wav", ".mka", ".weba", ".aiff",
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
    };

    // La fenêtre a été dessinée pour cette largeur utile. Plus large ou plus étroite, tout son contenu
    // grossit ou rétrécit dans la même proportion, la grille reste celle du dessin.
    private const double LargeurDessinee = 844;
    // Hauteur de la fenêtre au repos à l'échelle 1, pour choisir une largeur de départ qui tienne sur un petit écran.
    private const double HauteurAuRepos = 640;
    private string _largeurNotee = "";

    public FenetrePrincipale()
    {
        InitializeComponent();

#if PORTABLE
        // Sous Linux la fenêtre garde la taille de son dessin, et le journal a sa propre liste de lignes.
        LignesDuJournal.ItemsSource = _journal;
#else
        // La hauteur suit le contenu. Sur un petit écran la fenêtre s'arrête à la zone de travail et son contenu défile.
        MaxHeight = SystemParameters.WorkArea.Height;

        // La largeur se règle en tirant le bord, et elle est retenue. La première fois, la fenêtre s'ouvre un peu
        // plus grande que son dessin, sans dépasser ce que l'écran peut montrer en hauteur.
        var ecran = SystemParameters.WorkArea;
        _largeurNotee = Reglages.Lire("largeur", "");
        var largeur = double.TryParse(_largeurNotee, NumberStyles.Float, CultureInfo.InvariantCulture, out var notee)
            ? notee
            : Math.Min(1000, LargeurDessinee * ecran.Height * 0.82 / HauteurAuRepos + 16);
        Width = Math.Clamp(Math.Min(largeur, ecran.Width), MinWidth, MaxWidth);
#endif

        TexteVersionDeLAppli.Text = $"version {VersionDeLApplication.Lisible(VersionDeLApplication.Actuelle)}";

        Elements.ItemsSource = _elements;
#if !PORTABLE
        // Même règle pour Ctrl+V que pour le bouton Coller : des liens sur plusieurs lignes arrivent sur une seule.
        // Sans cela le champ ne garderait que la première ligne et perdrait les autres liens.
        DataObject.AddPastingHandler(ChampAdresse, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string texte || !texte.Contains('\n')) return;
            e.CancelCommand();
            ChampAdresse.SelectedText = string.Join(' ', texte.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            ChampAdresse.CaretIndex = ChampAdresse.SelectionStart + ChampAdresse.SelectionLength;
            ChampAdresse.SelectionLength = 0;
        });
#endif

        MontrerLeDossier(Reglages.LireDossier());
        ChoixQualite.ItemsSource = YtDlp.Qualites;
        ChoixQualite.SelectedIndex = 0;
        ChoixFormat.SelectionChanged += (_, _) =>
        {
            RetenirLeFormat();
            AdapterLeNiveau();
        };
        AdapterLesListes();

        // Les deux options, relues telles que laissées la dernière fois, et notées dès qu'on les change.
        // De 1 à 5. Les choix de 6 à 10 ont été retirés le 22/11/2026 : à dix à la fois, YouTube refusait la
        // connexion dès le départ, et chaque nouvel essai relançait le refus. Un réglage plus haut, retenu d'une
        // version précédente, est ramené à 5.
        ChoixSimultanes.ItemsSource = Enumerable.Range(1, SimultanesAuPlus).ToList();
        ChoixSimultanes.SelectedItem = int.TryParse(Reglages.Lire("simultanes", "5"), out var simultanes) && simultanes >= 1
            ? Math.Min(simultanes, SimultanesAuPlus)
            : SimultanesAuPlus;
        ChoixSimultanes.SelectionChanged += (_, _) => Reglages.Ecrire("simultanes", Simultanes.ToString(CultureInfo.InvariantCulture));
        ChoixQualiteSon.ItemsSource = YtDlp.QualitesDuSon;
        var qualiteNotee = Reglages.Lire("qualite-du-son", "0");
        ChoixQualiteSon.SelectedItem = YtDlp.QualitesDuSon.FirstOrDefault(q => q.Valeur == qualiteNotee) ?? YtDlp.QualitesDuSon[0];
        ChoixQualiteSon.SelectionChanged += (_, _) => Reglages.Ecrire("qualite-du-son", (ChoixQualiteSon.SelectedItem as Choix)?.Valeur ?? "0");
        // L'égaliseur est retenu par son nom, pas par ses filtres, pour qu'un réglage affiné plus tard reste reconnu.
        ChoixEgaliseur.ItemsSource = YtDlp.Egaliseurs;
        var egaliseurNote = Reglages.Lire("egaliseur", "");
        ChoixEgaliseur.SelectedItem = YtDlp.Egaliseurs.FirstOrDefault(e => e.Libelle == egaliseurNote) ?? YtDlp.Egaliseurs[0];
        ChoixEgaliseur.SelectionChanged += (_, _) => Reglages.Ecrire("egaliseur", (ChoixEgaliseur.SelectedItem as Choix)?.Libelle ?? "");
        // L'interrupteur du niveau égalisé, noté à chaque téléchargement. Qui avait choisi l'ancien réglage
        // « Volume égalisé » de l'égaliseur, que l'interrupteur remplace, le retrouve allumé.
        ChoixNiveau.IsChecked = Reglages.Lire("niveau", egaliseurNote == YtDlp.AncienVolumeEgalise ? "oui" : "non") == "oui";
        AdapterLesListes();

        Loaded += async (_, _) =>
        {
            ChampAdresse.Focus();
            await Travailler(annulable: false, () => VerifierYtDlpAsync());
#if PORTABLE
            // Lancée depuis le gestionnaire de fichiers ou le navigateur avec un lien magnet ou un fichier .torrent :
            // l'adresse est déjà dans le champ, il ne reste qu'à cliquer.
            if (AdressesDeDepart.Length > 0)
            {
                ChampAdresse.Text = string.Join(' ', AdressesDeDepart.Select(a => File.Exists(a) ? new Uri(Path.GetFullPath(a)).AbsoluteUri : a));
                ChampAdresse.CaretIndex = ChampAdresse.Text.Length;
            }
#endif
            RegarderLePressePapiers(this, EventArgs.Empty);
            // Sans attendre la réponse : la fenêtre est déjà utilisable, l'avis arrivera s'il y a lieu.
            _ = ChercherUneNouvelleVersionAsync();
        };
        Closing += FermerLaFenetre;
    }

#if PORTABLE
    // Sous Windows la progression se voit aussi sur l'icône de la barre des tâches. Linux n'a pas d'équivalent
    // commun à tous les bureaux, la valeur est simplement gardée.
    private readonly BarreDesTachesFactice BarreDesTaches = new();

    /// <summary>
    /// Ce que le système a passé à l'application en la lançant : un lien magnet, ou le chemin d'un fichier .torrent
    /// ouvert depuis le gestionnaire de fichiers (voir l'entrée de menu du paquet).
    /// </summary>
    internal static string[] AdressesDeDepart = [];

    /// <summary>Une erreur que rien n'a prévue : elle est dite dans le bandeau et notée au journal, l'application continue.</summary>
    internal void DireUneErreurImprevue(string quoi)
    {
        Ecrire($"ERROR: erreur inattendue. {quoi}");
        MontrerLeBandeau($"Une erreur inattendue s'est produite. {quoi} Vous pouvez continuer ou relancer l'application.");
    }

    // Les lignes du journal, que la liste de la fenêtre montre au fur et à mesure.
    private sealed record LigneDeJournal(string Texte, bool Rouge, bool DeLOutil);

    private readonly ObservableCollection<LigneDeJournal> _journal = [];
#else
    /// <summary>La barre de titre prend la couleur du fond, pour que la fenêtre soit d'un seul tenant.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var poignee = new WindowInteropHelper(this).Handle;
        // Couleurs au format de Windows, 0x00BBVVRR. Sans effet avant Windows 11, où la barre reste sombre standard.
        var fond = 0x00141414;      // #141414
        var texte = 0x00A3A3A3;     // #A3A3A3
        _ = DwmSetWindowAttribute(poignee, DWMWA_CAPTION_COLOR, ref fond, sizeof(int));
        _ = DwmSetWindowAttribute(poignee, DWMWA_TEXT_COLOR, ref texte, sizeof(int));

        // La fenêtre se redimensionne en largeur seulement, sa hauteur suit toujours son contenu.
        // Le bouton Agrandir n'aurait pas de sens ici, il est retiré.
        var style = GetWindowLongPtr(poignee, GWL_STYLE).ToInt64();
        _ = SetWindowLongPtr(poignee, GWL_STYLE, new IntPtr(style & ~WS_MAXIMIZEBOX));
        HwndSource.FromHwnd(poignee)?.AddHook(EcouterWindows);
    }

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x10000;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_EXITSIZEMOVE = 0x0232;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr fenetre, int attribut, ref int valeur, int taille);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr fenetre, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr fenetre, int index, IntPtr valeur);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr fenetre, int message, IntPtr w, IntPtr l);

    /// <summary>
    /// Deux messages de Windows. Le premier demande quel bord de la fenêtre est sous la souris : le haut, le bas
    /// et les coins sont ramenés aux côtés, pour que seule la largeur se tire. Le second dit qu'un déplacement ou
    /// un redimensionnement vient de finir : la hauteur se recale sur le contenu et la largeur est retenue.
    /// </summary>
    private IntPtr EcouterWindows(IntPtr fenetre, int message, IntPtr w, IntPtr l, ref bool traite)
    {
        if (message == WM_NCHITTEST)
        {
            var zone = DefWindowProc(fenetre, message, w, l).ToInt32();
            var corrigee = zone switch
            {
                12 => 2,            // bord haut, devient la barre de titre
                15 => 18,           // bord bas, devient un bord inerte
                13 or 16 => 10,     // coins de gauche, deviennent le bord gauche
                14 or 17 => 11,     // coins de droite, deviennent le bord droit
                _ => zone,
            };
            if (corrigee == zone) return IntPtr.Zero;
            traite = true;
            return new IntPtr(corrigee);
        }

        if (message == WM_EXITSIZEMOVE)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (WindowState != WindowState.Normal) return;
                // Tirer le bord a fait passer la fenêtre en taille libre. Elle reprend sa hauteur automatique.
                SizeToContent = SizeToContent.Height;
                var largeur = Width.ToString("0", CultureInfo.InvariantCulture);
                if (largeur != _largeurNotee) Reglages.Ecrire("largeur", _largeurNotee = largeur);
                GarderLaFenetreALEcran();
            });
        }
        return IntPtr.Zero;
    }

    /// <summary>La zone utile a changé de largeur : tout le contenu prend l'échelle correspondante.</summary>
    private void AdapterLEchelle(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || e.NewSize.Width <= 0) return;
        Echelle.ScaleX = Echelle.ScaleY = Math.Clamp(e.NewSize.Width / LargeurDessinee, 0.8, 1.8);
    }
#endif

    // ───────────────────────────── Listes de choix ─────────────────────────────

    // La vue de téléchargement choisie, audio ou vidéo. L'application s'ouvre sur l'audio. La valeur reste
    // quand on passe sur les torrents, pour savoir où revenir et quoi faire d'une adresse ordinaire.
    private bool _audio = true;

    // Le format choisi dans chaque vue, retrouvé quand on y revient.
    private int _formatAudio, _formatVideo;

    // Vrai pendant que la liste des formats est remplacée. Ce qui s'y sélectionne alors n'est pas un choix de l'utilisateur.
    private bool _formatsEnChangement;

    /// <summary>Retient le format choisi, dans la vue où il l'a été.</summary>
    private void RetenirLeFormat()
    {
        if (_formatsEnChangement || ChoixFormat.SelectedIndex < 0) return;
        if (ReferenceEquals(ChoixFormat.ItemsSource, YtDlp.FormatsAudio)) _formatAudio = ChoixFormat.SelectedIndex;
        else if (ReferenceEquals(ChoixFormat.ItemsSource, YtDlp.FormatsVideo)) _formatVideo = ChoixFormat.SelectedIndex;
    }

    /// <summary>
    /// Met les réglages à l'image de la vue, audio ou vidéo : ses formats, et sa qualité à elle. Chaque vue ne
    /// montre que ce qui la concerne. Sans ffmpeg aucune conversion n'est possible, la liste ne propose alors
    /// que le format d'origine du site.
    /// </summary>
    private void AdapterLesListes()
    {
        // Les touches préviennent dès la lecture du XAML, avant que les listes existent.
        if (ChoixFormat is null || ChoixQualite is null) return;

        var audio = _audio;
        var avecFfmpeg = _complements.Ffmpeg is not null;
        Choix[] formats = !avecFfmpeg ? [YtDlp.FormatDOrigine] : audio ? YtDlp.FormatsAudio : YtDlp.FormatsVideo;

        if (!ReferenceEquals(ChoixFormat.ItemsSource, formats))
        {
            // Le format voulu est lu avant de remplacer la liste. « Format d'origine du site » figure dans toutes :
            // s'il était choisi, la liste le garde en changeant de contenu, et il passerait pour le choix de la vue
            // (vu aux essais sous Linux, où l'application s'ouvrait dessus au lieu du MP3).
            var voulu = Math.Clamp(audio ? _formatAudio : _formatVideo, 0, formats.Length - 1);
            _formatsEnChangement = true;
            ChoixFormat.ItemsSource = formats;
            ChoixFormat.SelectedIndex = voulu;
            _formatsEnChangement = false;
        }
        ChoixFormat.IsEnabled = avecFfmpeg;
        // Le groupe porte le nom de ce qu'il règle, et sa seconde liste celui de la qualité qui va avec la vue.
        if (TitreDuFichier is not null) TitreDuFichier.Text = audio ? "SON" : "IMAGE";
        if (EtiquetteQualite is not null) EtiquetteQualite.Text = audio ? "QUALITÉ" : "DÉFINITION";
        // La définition de l'image pour la vidéo. Le débit du son et l'égaliseur pour l'audio, qui demandent ffmpeg.
        ChoixQualite.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        if (ChoixQualiteSon is not null)
        {
            ChoixQualiteSon.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            ChoixQualiteSon.IsEnabled = avecFfmpeg;
        }
        if (ChoixEgaliseur is not null) ChoixEgaliseur.IsEnabled = avecFfmpeg;
        if (LigneEgaliseur is not null) Derouler(LigneEgaliseur, audio);
        AdapterLeNiveau();
    }

    // Le niveau égalisé, tel que choisi au clic sur Télécharger et gardé pour tout le travail, avec le format des morceaux.
    private bool _niveau;
    private string _formatDuNiveau = "";

    /// <summary>L'interrupteur du niveau ne s'offre que pour un format qu'on sait égaliser sans perte, et avec ffmpeg pour mesurer.</summary>
    private void AdapterLeNiveau()
    {
        if (ChoixNiveau is null || ChoixFormat is null) return;
        ChoixNiveau.IsEnabled = _complements.Ffmpeg is not null
                                && NiveauSonore.PrendEnCharge((ChoixFormat.SelectedItem as Choix)?.Valeur ?? "");
    }

    /// <summary>
    /// Égalise le niveau d'un morceau qui vient d'arriver, et dit au journal ce qui a été fait. Un échec laisse
    /// le fichier tel qu'il est arrivé : mieux vaut un morceau au niveau d'origine qu'un morceau abîmé.
    /// </summary>
    /// <param name="rang">Le rang du morceau dans la liste, pour sa ligne de journal. Null pour un téléchargement seul.</param>
    /// <param name="suivre">Reçoit le ffmpeg en marche, pour qu'Annuler l'arrête avec le reste.</param>
    private async Task EgaliserLeNiveauAsync(string fichier, int? rang, Action<Process?> suivre)
    {
        if (_complements.Ffmpeg is not { } ffmpeg) return;
        string ligne;
        try
        {
            if (await NiveauSonore.EgaliserAsync(ffmpeg, fichier, _formatDuNiveau, suivre) is not { } bilan) return;
            ligne = $"Niveau sonore, {bilan}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            ligne = $"Niveau sonore non égalisé, le fichier reste tel quel. {e.Message}";
        }
        // Annulé pendant la mesure : ffmpeg a été arrêté, il n'y a rien à en dire.
        if (_annulationDemandee) return;
        if (rang is { } numero) Ecrire(ligne, numero);
        else Ecrire(ligne);
    }

    // ───────────────────────────── Petites actions ─────────────────────────────

#if PORTABLE
    private async void CollerLAdresse(object sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } pressePapiers || await pressePapiers.TryGetTextAsync() is not { Length: > 0 } texte) return;
        // Plusieurs liens copiés sur plusieurs lignes tiennent sur une seule, séparés par des espaces.
        var collage = string.Join(' ', texte.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        ChampAdresse.Text = collage;
        ChampAdresse.CaretIndex = collage.Length;
        ChampAdresse.Focus();
    }
#else
    private void CollerLAdresse(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            // Plusieurs liens copiés sur plusieurs lignes tiennent sur une seule, séparés par des espaces.
            ChampAdresse.Text = string.Join(' ', Clipboard.GetText().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            ChampAdresse.CaretIndex = ChampAdresse.Text.Length;
            ChampAdresse.Focus();
        }
        catch (COMException)
        {
            // Le presse-papiers est tenu par un autre programme à cet instant, un second clic suffira.
        }
    }
#endif

#if PORTABLE
    private async void ChoisirLeDossier(object sender, RoutedEventArgs e)
    {
        var choisis = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Dossier où ranger les téléchargements",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(_dossier) ? await StorageProvider.TryGetFolderFromPathAsync(_dossier) : null,
        });
        if (choisis.Count == 0 || choisis[0].TryGetLocalPath() is not { Length: > 0 } dossier) return;
        MontrerLeDossier(dossier);
        Reglages.EcrireDossier(dossier);
    }
#else
    private void ChoisirLeDossier(object sender, RoutedEventArgs e)
    {
        var dialogue = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dossier où ranger les téléchargements",
            InitialDirectory = _dossier,
        };
        if (dialogue.ShowDialog(this) != true) return;
        MontrerLeDossier(dialogue.FolderName);
        Reglages.EcrireDossier(dialogue.FolderName);
    }
#endif

    /// <summary>Affiche le nom du dossier en clair et le chemin qui y mène en plus discret, plus lisible qu'un long chemin coupé.</summary>
    private void MontrerLeDossier(string dossier)
    {
        _dossier = dossier;
        var court = Path.TrimEndingDirectorySeparator(dossier);
        var nom = Path.GetFileName(court);
        var parent = nom.Length > 0 ? Path.GetDirectoryName(court) ?? "" : "";
        // Un chemin trop long garde son début et sa fin, les deux bouts qui servent à se repérer.
        if (parent.Length > 64) parent = string.Concat(parent.AsSpan(0, 28), " … ", parent.AsSpan(parent.Length - 32));
        NomDossier.Text = nom.Length > 0 ? nom : dossier;
        CheminParent.Text = parent;
        LigneDossier.ToolTip = dossier;
        // Le chemin complet est lu par les lecteurs d'écran quand le bouton prend le clavier.
        AutomationProperties.SetHelpText(BoutonParcourir, $"Dossier actuel {dossier}");
    }

    private void OuvrirLeDossierDeDestination(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_dossier)) LancerLExplorateur(_dossier);
        else Etat("Ce dossier n'existe pas encore, il sera créé au premier téléchargement.", annoncer: true);
    }

    private void OuvrirLeDossierDesOutils(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(YtDlp.DossierOutils);
            LancerLExplorateur(YtDlp.DossierOutils);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Etat($"Impossible d'ouvrir le dossier des outils. {ex.Message}", Ton.Erreur, annoncer: true);
        }
    }

    private void OuvrirLeFichier(object sender, RoutedEventArgs e)
    {
        if (_dernierFichier is null || !File.Exists(_dernierFichier))
        {
            Etat("Ce fichier n'est plus à cet endroit.", Ton.Erreur, annoncer: true);
            return;
        }
        if (!ExtensionsDeMedia.Contains(Path.GetExtension(_dernierFichier)))
        {
            // Un fichier d'un type inattendu n'est pas lancé, on le montre seulement.
            MontrerLeFichier(sender, e);
            return;
        }
        try
        {
            // Ouverture par le lecteur que Windows associe à ce type de fichier.
            Process.Start(new ProcessStartInfo(_dernierFichier) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Etat($"Windows n'a pas pu ouvrir ce fichier. {ex.Message}", Ton.Erreur, annoncer: true);
        }
    }

#if PORTABLE
    // Sous Linux il n'existe pas de moyen commun à tous les gestionnaires de fichiers de sélectionner un fichier :
    // on ouvre le dossier qui le contient.
    private void MontrerLeFichier(object sender, RoutedEventArgs e)
    {
        if (_dernierFichier is not null && Path.GetDirectoryName(_dernierFichier) is { Length: > 0 } dossier && Directory.Exists(dossier))
            LancerLExplorateur(dossier);
        else OuvrirLeDossierDeDestination(sender, e);
    }

    private void LancerLExplorateur(string? dossier, string? argumentsBruts = null)
    {
        try
        {
            if (dossier is not null) Plateforme.Ouvrir(dossier);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Etat($"Impossible d'ouvrir le dossier. {ex.Message}", Ton.Erreur, annoncer: true);
        }
    }
#else
    private void MontrerLeFichier(object sender, RoutedEventArgs e)
    {
        if (_dernierFichier is null || !File.Exists(_dernierFichier))
        {
            OuvrirLeDossierDeDestination(sender, e);
            return;
        }
        // L'Explorateur attend « /select, » suivi du chemin entre guillemets, d'un seul tenant.
        // Un nom de fichier Windows ne peut pas contenir de guillemet, le chemin ne peut donc pas en sortir.
        LancerLExplorateur(null, $"/select,\"{_dernierFichier}\"");
    }

    private void LancerLExplorateur(string? dossier, string? argumentsBruts = null)
    {
        try
        {
            // Chemin complet de l'Explorateur de Windows. Un nom nu pourrait désigner un fichier du même nom posé ailleurs.
            var explorateur = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var demarrage = new ProcessStartInfo(explorateur) { UseShellExecute = false };
            if (argumentsBruts is not null) demarrage.Arguments = argumentsBruts;
            else demarrage.ArgumentList.Add(dossier!);   // argument séparé, l'Explorateur ne l'interprète pas
            Process.Start(demarrage)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Etat($"Impossible d'ouvrir l'Explorateur. {ex.Message}", Ton.Erreur, annoncer: true);
        }
    }
#endif

    private void BasculerLeJournal(object sender, RoutedEventArgs e)
    {
        var ouvert = BasculeJournal.IsChecked == true;
        Derouler(Journal, ouvert);
        BasculeJournal.Content = ouvert ? "Masquer le _journal" : "Afficher le _journal";
        if (!ouvert) return;
        Journal.ScrollToEnd();
        GarderLaFenetreALEcran();
    }

    private void BasculerLesElements(object sender, RoutedEventArgs e)
    {
        var ouvert = BasculeElements.IsChecked == true;
        Derouler(CadreElements, ouvert);
        BasculeElements.Content = ouvert ? "Masquer le _détail" : "Afficher le _détail";
        if (ouvert) GarderLaFenetreALEcran();
    }

    // ───────────────────────────── Mouvements ─────────────────────────────

#if PORTABLE
    /// <summary>Sous Linux, pas encore de mouvement : l'élément se montre ou se cache d'un coup.</summary>
    private static void Derouler(Control element, bool montrer) =>
        element.Visibility = montrer ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Sous Linux, pas encore de mouvement à l'entrée d'une vue.</summary>
    private static void FaireEntrer(Control vue)
    {
    }
#else
    // Les mouvements sont courts : ils accompagnent le geste, ils ne font pas attendre.
    private static readonly Duration DureeDuMouvement = TimeSpan.FromMilliseconds(190);
    private static readonly IEasingFunction Amorti = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Les éléments en train de se déplier ou de se replier : vers quoi ils vont, et ce que le dessin leur fixait
    // (hauteur, hauteur minimale, découpe), à leur rendre une fois le mouvement fini.
    private readonly Dictionary<FrameworkElement, (bool Montrer, object Hauteur, object HauteurMin, bool Decoupe)> _plis = [];

    private bool MouvementsPermis => IsLoaded && SystemParameters.ClientAreaAnimation;

    private static void Rendre(FrameworkElement element, DependencyProperty propriete, object valeur)
    {
        if (valeur == DependencyProperty.UnsetValue) element.ClearValue(propriete);
        else element.SetValue(propriete, valeur);
    }

    /// <summary>
    /// Montre ou cache un élément en le déroulant : sa hauteur s'ouvre ou se referme, son contenu apparaît ou
    /// s'efface. La fenêtre, qui épouse son contenu, suit le mouvement. Une demande contraire en cours de route
    /// repart de là où l'élément en est.
    /// </summary>
    private void Derouler(FrameworkElement element, bool montrer)
    {
        var enRoute = _plis.TryGetValue(element, out var pli);
        if ((enRoute ? pli.Montrer : element.Visibility == Visibility.Visible) == montrer) return;

        // Fenêtre pas encore à l'écran, ou animations coupées dans les réglages de Windows : pas de mouvement.
        if (!enRoute && !MouvementsPermis)
        {
            element.Visibility = montrer ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (!enRoute)
            pli = (montrer, element.ReadLocalValue(HeightProperty), element.ReadLocalValue(MinHeightProperty), element.ClipToBounds);
        _plis[element] = pli = (montrer, pli.Hauteur, pli.HauteurMin, pli.Decoupe);
        element.ClipToBounds = true;

        if (!montrer)
        {
            element.MinHeight = 0;
            Animer(element, element.ActualHeight, 0, montrer: false, fondreDepuisLeDebut: !enRoute);
            return;
        }

        if (!enRoute)
        {
            // Présent mais fermé et invisible, le temps que ce qui l'a fait apparaître finisse de le remplir.
            element.MinHeight = 0;
            element.Height = 0;
            element.Opacity = 0;
            element.Visibility = Visibility.Visible;
        }
        var depart = enRoute ? element.ActualHeight : 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Un contre-ordre est arrivé entre-temps.
            if (!_plis.TryGetValue(element, out var courant) || !courant.Montrer) return;
            // La hauteur d'arrivée est celle que l'élément prend de lui-même, mesurée sans la contrainte du mouvement.
            element.BeginAnimation(HeightProperty, null);
            Rendre(element, HeightProperty, courant.Hauteur);
            Rendre(element, MinHeightProperty, courant.HauteurMin);
            var largeur = (VisualTreeHelper.GetParent(element) as FrameworkElement)?.ActualWidth ?? ActualWidth;
            element.Measure(new Size(largeur, double.PositiveInfinity));
            var arrivee = Math.Max(0, element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom);
            element.MinHeight = 0;
            Animer(element, depart, arrivee, montrer: true, fondreDepuisLeDebut: !enRoute);
        });
    }

    private void Animer(FrameworkElement element, double depart, double arrivee, bool montrer, bool fondreDepuisLeDebut)
    {
        var hauteur = new DoubleAnimation(depart, arrivee, DureeDuMouvement) { EasingFunction = Amorti };
        hauteur.Completed += (_, _) =>
        {
            // Une demande contraire a pris le relais, c'est elle qui finira le travail.
            if (!_plis.TryGetValue(element, out var dernier) || dernier.Montrer != montrer) return;
            _plis.Remove(element);
            if (!montrer) element.Visibility = Visibility.Collapsed;
            element.BeginAnimation(HeightProperty, null);
            element.BeginAnimation(OpacityProperty, null);
            Rendre(element, HeightProperty, dernier.Hauteur);
            Rendre(element, MinHeightProperty, dernier.HauteurMin);
            element.ClipToBounds = dernier.Decoupe;
            element.Opacity = 1;
            if (montrer) GarderLaFenetreALEcran();
        };
        var fondu = new DoubleAnimation(montrer ? 1 : 0, DureeDuMouvement);
        if (fondreDepuisLeDebut) fondu.From = montrer ? 0 : 1;
        element.BeginAnimation(HeightProperty, hauteur);
        element.BeginAnimation(OpacityProperty, fondu);
    }

    /// <summary>Une vue qui arrive à l'écran : elle monte de quelques points en apparaissant.</summary>
    private void FaireEntrer(FrameworkElement vue)
    {
        if (!MouvementsPermis) return;
        var glissement = new TranslateTransform(0, 10);
        vue.RenderTransform = glissement;
        glissement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, DureeDuMouvement) { EasingFunction = Amorti });
        vue.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, DureeDuMouvement));
    }
#endif

#if PORTABLE
    /// <summary>Sous Linux c'est le gestionnaire de fenêtres qui garde la fenêtre à l'écran, il n'y a rien à faire.</summary>
    private static void GarderLaFenetreALEcran()
    {
    }
#else
    /// <summary>La fenêtre vient de grandir vers le bas. Si elle dépasse de l'écran, on la remonte.</summary>
    private void GarderLaFenetreALEcran() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var bas = SystemParameters.WorkArea.Bottom;
        if (Top + ActualHeight > bas) Top = Math.Max(SystemParameters.WorkArea.Top, bas - ActualHeight);
    });
#endif

    // ───────────────────────────── Outils présents ou non ─────────────────────────────

    /// <summary>
    /// S'assure que yt-dlp est là et répond. À la première ouverture sur un PC, dépose d'abord les programmes
    /// que l'exécutable transporte. Rend le chemin de yt-dlp, ou null après avoir affiché un bandeau
    /// qui dit où poser le fichier et où le trouver.
    /// </summary>
    private async Task<string?> VerifierYtDlpAsync()
    {
        var vientDEtreDeploye = false;
#if PORTABLE
        // Sous Linux rien n'est embarqué : ffmpeg et aria2 viennent du système, et les trois outils qui doivent
        // rester très récents sont pris à leurs dépôts officiels à la première ouverture, empreinte contrôlée.
        if (!Plateforme.Windows && MiseAJourDesOutils.OutilsLinuxIncomplets())
        {
            vientDEtreDeploye = true;
            try
            {
                using var client = MiseAJourDesOutils.NouveauClient();
                foreach (var (nom, tache) in MiseAJourDesOutils.OutilsLinux)
                {
                    Etat($"Première ouverture, installation de {nom} depuis son dépôt officiel…", annoncer: true);
                    var avancement = new Progress<MiseAJourDesOutils.Avancement>(a =>
                        Etat(a.Total > 0 ? $"Première ouverture, installation de {nom}, {Octets(a.Recu)} sur {Octets(a.Total)}…" : $"Première ouverture, installation de {nom}…"));
                    Ecrire(await tache(client, avancement, CancellationToken.None));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
            {
                MontrerLeBandeau($"Les outils n'ont pas pu être installés. {ex.Message} Vérifiez la connexion à Internet, puis relancez l'application.");
                return null;
            }
        }
#else
        if (!ChargeEmbarquee.EstDeployee())
        {
            vientDEtreDeploye = true;
            Etat("Préparation des outils pour la première ouverture, quelques secondes…", annoncer: true);
            try
            {
                // Progress renvoie chaque étape vers le fil de la fenêtre, le dépôt lui-même se fait à côté.
                IProgress<(int Fait, int Total)> avancement = new Progress<(int Fait, int Total)>(p =>
                    Etat($"Préparation des outils pour la première ouverture, {p.Fait} sur {p.Total}…"));
                await Task.Run(() => ChargeEmbarquee.Deployer((fait, total) => avancement.Report((fait, total))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MontrerLeBandeau($"Impossible de déposer les outils dans le dossier {YtDlp.DossierOutils}. {ex.Message} "
                                 + "Libérez de la place ou vérifiez que l'antivirus ne bloque pas ce dossier, puis relancez l'application.");
                return null;
            }
        }
#endif

        var executable = YtDlp.Trouver();
        var version = executable is null ? null : await YtDlp.LireVersionAsync(executable);

        // Précaution après un dépôt. Sur certains PC l'antivirus examine le fichier tout neuf et peut en retarder
        // le lancement. On réessaie donc deux fois avant de conclure qu'il ne répond pas.
        for (var essai = 0; vientDEtreDeploye && executable is not null && version is null && essai < 2; essai++)
        {
            Etat("Vérification des outils…");
            await Task.Delay(2000);
            version = await YtDlp.LireVersionAsync(executable);
        }
        if (vientDEtreDeploye) Etat("");

        if (executable is null || version is null)
        {
            MontrerLeBandeau(executable is null
                ? $"Le fichier {YtDlp.NomExecutable} est introuvable. Il doit se trouver dans le dossier {YtDlp.DossierOutils}. "
                  + $"Téléchargez-le depuis la page officielle {YtDlp.PageOfficielle}, placez-le dans ce dossier, puis cliquez de nouveau sur Télécharger."
                : $"Le fichier {executable} est présent mais ne répond pas. "
                  + $"Remplacez-le par une copie neuve prise sur la page officielle {YtDlp.PageOfficielle}.");
            TexteVersion.Text = "yt-dlp introuvable";
            VersionFfmpeg.Text = VersionDeno.Text = VersionSpotDl.Text = VersionGalleryDl.Text = "";
            return null;
        }

        Bandeau.Visibility = Visibility.Collapsed;
        _complements = YtDlp.TrouverComplements();
        AdapterLesListes();

        // Pied de fenêtre, une mention par outil.
        var versions = MiseAJourDesOutils.LireVersions();
        var avant = TexteVersion.Text;
        TexteVersion.Text = $"yt-dlp {version}";
        VersionFfmpeg.Text = _complements.Ffmpeg is null ? "ffmpeg absent, pas de conversion"
            : versions.TryGetValue("ffmpeg", out var date) ? $"ffmpeg du {MiseAJourDesOutils.DateLisible(date)}" : "ffmpeg prêt";
        VersionDeno.Text = _complements.Deno is null ? "Deno absent, YouTube peut échouer"
            : versions.TryGetValue("deno", out var numero) ? $"Deno {numero.TrimStart('v')}" : "Deno prêt";
#if PORTABLE
        // Sous Linux spotDL n'est pas installé : les liens Spotify se lisent par la page publique ou par le compte.
        VersionSpotDl.Text = SpotDl.Trouver() is null ? ""
#else
        VersionSpotDl.Text = SpotDl.Trouver() is null ? "spotDL absent, pas de liens Spotify"
#endif
            : versions.TryGetValue("spotdl", out var spotdl) ? $"spotDL {spotdl.TrimStart('v')}" : "spotDL prêt";
        VersionGalleryDl.Text = GalleryDl.Trouver() is null ? "gallery-dl absent, pas d'images"
            : versions.TryGetValue("gallery-dl", out var galerie) ? $"gallery-dl {galerie.TrimStart('v')}" : "gallery-dl prêt";
        if (avant != TexteVersion.Text) Ecrire($"yt-dlp {version} est prêt.");
        return executable;
    }

    private void MontrerLeBandeau(string message)
    {
        TexteBandeau.Text = message;
        Bandeau.Visibility = Visibility.Visible;
        Etat("");
        Annoncer(TexteBandeau);
    }

    // ───────────────────────────── Télécharger, annuler, mettre à jour ─────────────────────────────

    /// <summary>
    /// Encadre tout travail long. Le verrou est pris dès le clic, avant toute attente, pour qu'un double clic
    /// ou la touche Entrée répétée ne lance jamais deux yt-dlp à la fois.
    /// </summary>
    private async Task Travailler(bool annulable, Func<Task> travail)
    {
        if (_occupe) return;
        _occupe = true;
        BoutonTelecharger.IsEnabled = false;
        BoutonMettreAJour.IsEnabled = false;
        BoutonParcourir.IsEnabled = false;
        BoutonReprendre.IsEnabled = false;
        BoutonNouvelleVersion.IsEnabled = false;
        BoutonAnnuler.Visibility = annulable ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            await travail();
        }
        finally
        {
            _occupe = false;
            _miseAJour = false;
            BoutonTelecharger.IsEnabled = true;
            BoutonMettreAJour.IsEnabled = true;
            BoutonParcourir.IsEnabled = true;
            BoutonReprendre.IsEnabled = true;
            BoutonNouvelleVersion.IsEnabled = true;
            BoutonAnnuler.Visibility = Visibility.Collapsed;
            BoutonAnnuler.IsEnabled = true;
        }
    }

    private async void Telecharger(object sender, RoutedEventArgs e)
    {
        if (_occupe) return;
        var adresses = YtDlp.AdressesValides(ChampAdresse.Text);
        if (adresses.Count == 0)
        {
            ViderLaZoneDEtat();
            Etat("Collez d'abord l'adresse complète de la vidéo. Elle commence par https://", Ton.Erreur, annoncer: true);
            ChampAdresse.Focus();
            return;
        }
        var adresse = adresses[0];

        var dossier = _dossier;
        try
        {
            Directory.CreateDirectory(dossier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ViderLaZoneDEtat();
            Etat($"Impossible d'utiliser le dossier de destination. {ex.Message}", Ton.Erreur, annoncer: true);
            return;
        }

        await Travailler(annulable: true, async () =>
        {
            ViderLaZoneDEtat();
            var executable = await VerifierYtDlpAsync();
            if (executable is null) return;

            _annulationDemandee = false;
            // Une nouvelle demande remplace la précédente, ses manquants ne sont plus proposés.
            _aReprendre = [];
            _aVerifier.Clear();
            _dossierDeLaListe = dossier;
            BoutonReprendre.Visibility = Visibility.Collapsed;
            _qualiteDuSon = (ChoixQualiteSon.SelectedItem as Choix)?.Valeur ?? "0";
            _egaliseur = _audio && ChoixEgaliseur.IsEnabled ? (ChoixEgaliseur.SelectedItem as Choix)?.Valeur ?? "" : "";
            // Le niveau égalisé ne vaut que pour le son, dans un format qu'on sait égaliser sans perte.
            _formatDuNiveau = (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "";
            _niveau = _audio && ChoixNiveau.IsEnabled && ChoixNiveau.IsChecked == true;
            Reglages.Ecrire("niveau", ChoixNiveau.IsChecked == true ? "oui" : "non");

            // Les torrents ont leur propre vue et leur propre outil. Ils y partent tout de suite, et la vue de
            // téléchargement reste libre : un torrent dure, on ne bloque pas le reste en l'attendant.
            var torrents = adresses.Where(Aria2.EstUnTorrent).ToList();
            if (torrents.Count > 0)
            {
                var autres = adresses.Where(a => !Aria2.EstUnTorrent(a)).ToList();
                ChampAdresse.Text = string.Join(' ', autres);
                var vue = OuvrirLesTorrents();
                foreach (var torrent in torrents) await vue.AjouterAsync(torrent, dossier);
                if (autres.Count == 0) return;
                // Des adresses ordinaires étaient collées avec : retour à la vue d'où l'on venait, audio ou vidéo, pour elles.
                (_audio ? ChoixAudio : ChoixVideo).IsChecked = true;
                // Des adresses ordinaires étaient collées avec. Elles suivent ici, par le chemin habituel.
                adresses = autres;
                adresse = autres[0];
            }

            // Plusieurs liens collés ensemble forment une file, menée de front.
            if (adresses.Count > 1)
            {
                await TelechargerPlusieursAsync(executable, adresses, dossier);
                return;
            }

            // Un lien Spotify suit son propre parcours, tout le reste va droit à yt-dlp.
            if (LiensDeMusique.PlateformeFermee(adresse) is { } fermee)
            {
                // Tidal, Qobuz et Amazon Music refusent la lecture de leurs listes à un programme sans compte.
                // Le dire tout de suite vaut mieux qu'un échec obscur de yt-dlp.
                Terminer("ERREUR", $"{fermee} ne laisse pas lire la liste de ses titres sans compte, ce lien ne peut pas être traité. "
                                   + "Les liens Spotify, Deezer, Apple Music, YouTube Music, SoundCloud et Bandcamp marchent.", Ton.Erreur);
                return;
            }
            if (LiensDeMusique.Plateforme(adresse) is not null)
            {
                await TelechargerDepuisSpotifyAsync(executable, adresse, dossier);
                return;
            }

            Ecrire("");
            Ecrire($"Téléchargement de {adresse}");

            // Un document ou une archive, reconnus à la fin de l'adresse (.pdf, .zip…), n'ont rien à faire dans
            // yt-dlp : mesuré le 22/11/2026, il les prend pour une vidéo, échoue à les convertir et laisse un
            // fichier parasite. Ils sont rapatriés tels quels, directement.
            if (FichierDirect.EstUnDocument(adresse))
            {
                Etat("Lecture de l'adresse…", annoncer: true);
                if (!await EssayerLeFichierDirectAsync(adresse, dossier))
                    Terminer("ERREUR", "Cette adresse n'a pas donné le fichier attendu (page d'erreur, accès refusé ou lien périmé).", Ton.Erreur);
                return;
            }

            var audioSeul = _audio;
            var format = (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "";
            var qualite = (ChoixQualite.SelectedItem as Choix)?.Valeur ?? "";

            // Case playlist cochée : l'application lit le sommaire, puis mène elle-même la liste, plusieurs vidéos à la fois.
            if (ChoixPlaylist.IsChecked == true)
            {
                Etat("Lecture de la playlist…", annoncer: true);
                BarreProgression.IsIndeterminate = true;
                BarreProgression.Visibility = Visibility.Visible;
                var (nomDeListe, videos) = await YtDlp.ListerLaPlaylistAsync(executable, adresse, _complements);
                if (_annulationDemandee)
                {
                    BarreProgression.Visibility = Visibility.Collapsed;
                    Terminer("ANNULÉ", "", Ton.Neutre);
                    return;
                }

                if (videos.Count > 0 && nomDeListe.Length > 0)
                {
                    Ecrire($"Playlist « {nomDeListe} », {videos.Count} {(videos.Count > 1 ? "vidéos" : "vidéo")}.");
                    var travaux = new List<Travail>();
                    for (var i = 0; i < videos.Count; i++)
                    {
                        var (video, titre) = videos[i];
                        var element = new ElementDePlaylist(i + 1);
                        if (titre.Length > 0) element.Titre = titre;
                        _elements.Add(element);
                        // Chaque vidéo est demandée seule, dans le dossier de la playlist et précédée de son rang.
                        var uneVideo = new Demande(video, audioSeul, format, qualite, dossier, YtDlp.PrefixeDeListe(nomDeListe, i + 1), _qualiteDuSon, _egaliseur);
                        travaux.Add(new Travail(element, "erreur", suivi => YtDlp.ArgumentsTelechargement(uneVideo, _complements, suivi)));
                    }
                    var (recus, echecs) = await MenerLaListeAsync(executable, travaux);
                    ConclureLaListe(recus, echecs);
                    return;
                }
                // Ce n'était pas une playlist. La vidéo seule suit le chemin habituel, sans sous-dossier.
            }

            var demande = new Demande(adresse, audioSeul, format, qualite, dossier, PrefixeDeNom: "", QualiteDuSon: _qualiteDuSon, Egaliseur: _egaliseur);

            YtDlp.EffacerLaTrace();
            Mesures.Visibility = Visibility.Visible;
            // Un clic sur Annuler pendant la lecture de la playlist est honoré ici, avant de lancer le transfert.
            var code = _annulationDemandee
                ? -1
                : await LancerAsync(executable, YtDlp.ArgumentsTelechargement(demande, _complements), "Recherche de la vidéo…");
            var fichiers = _annulationDemandee ? [] : YtDlp.LireLaTrace();

            // yt-dlp n'a rien pris : une erreur, ou une image ou un document qu'il a écartés. Avant de conclure, la
            // cascade continue. D'abord gallery-dl, pour les adresses qui portent des images (en mode Audio il n'a
            // rien à offrir, et un PDF ou une archive ne le concernent pas). Puis, en dernier, le fichier tel quel.
            var imagesEssayees = false;
            if (!_annulationDemandee && fichiers.Count == 0 && (code != 0 || _imageEcartee) && _refusDeYoutube == 0 && GalleryDl.PeutAider(adresse))
            {
                if (!audioSeul && !FichierDirect.EstUnDocument(adresse) && GalleryDl.Trouver() is { } galerie)
                {
                    imagesEssayees = true;
                    if (await EssayerLesImagesAsync(galerie, adresse, dossier)) return;
                }
                if (await EssayerLeFichierDirectAsync(adresse, dossier)) return;
            }

            if (_annulationDemandee)
            {
                BarreProgression.Visibility = Visibility.Collapsed;
                MarquerLElementCourant("annulé");
                Terminer("ANNULÉ", "La partie déjà reçue reste dans le dossier, relancer le téléchargement le reprendra où il s'est arrêté.", Ton.Neutre);
            }
            else if (fichiers.Count > 0)
            {
                // Niveau égalisé, pour ce qui vient d'arriver. Un fichier déjà présent a été traité à son arrivée.
                if (_niveau && !_dejaPresent)
                {
                    Etat("Mesure du niveau sonore…");
                    foreach (var recu in fichiers)
                    {
                        if (_annulationDemandee) break;
                        await EgaliserLeNiveauAsync(recu, null, processus => _enCours = processus);
                    }
                    Etat("");
                }
                // La barre finit pleine et verte, le nom du fichier remplace les chiffres.
                if (code == 0) MarquerLElementCourant("terminé");
                BarreProgression.IsIndeterminate = false;
                BarreProgression.Value = BarreProgression.Maximum;
                BarreProgression.SetResourceReference(ForegroundProperty, "Reussite");
                MesureReste.Text = "00:00";
                _dernierFichier = fichiers[^1];
                var nom = Path.GetFileName(_dernierFichier);
                NomFichier.Text = fichiers.Count == 1 ? nom : $"{fichiers.Count} fichiers, le dernier est {nom}";
                NomFichier.ToolTip = _dernierFichier;
                Derouler(Resultat, true);
                // Dans une playlist, quelques vidéos peuvent manquer (privées, retirées) sans que le reste soit perdu.
                if (code == 0 && _dejaPresent && fichiers.Count == 1)
                {
                    Mesures.Visibility = Visibility.Collapsed;
                    Terminer("DÉJÀ LÀ", "Ce fichier était déjà dans le dossier, il n'a pas été téléchargé de nouveau. "
                                        + "Pour l'obtenir dans une autre qualité, déplacez ou supprimez d'abord l'ancien.", Ton.Neutre);
                }
                else if (code == 0) Terminer("TERMINÉ", "", Ton.Reussite);
                else Terminer("TERMINÉ", $"{fichiers.Count} fichiers reçus, mais certaines vidéos ont échoué. Le détail est dans le journal.", Ton.Erreur, motEnVert: true);
            }
            else if (code == 0)
            {
                BarreProgression.Visibility = Visibility.Collapsed;
                Mesures.Visibility = Visibility.Collapsed;
                Terminer("", _imageEcartee
                    ? "Cette adresse ne mène ni à une vidéo ni à du son, et ce qu'elle porte n'a pas pu être récupéré. Le détail est dans le journal."
                    : "yt-dlp a terminé sans rien télécharger de nouveau, le détail est dans le journal.", Ton.Neutre);
            }
            else
            {
                BarreProgression.Visibility = Visibility.Collapsed;
                Mesures.Visibility = Visibility.Collapsed;
                MarquerLElementCourant("erreur");
                BasculeJournal.IsChecked = true;   // le détail de l'erreur est là, autant le montrer
                if (_refusDeYoutube > 0)
                {
                    Terminer("BLOQUÉ", MessageDuBlocage, Ton.Erreur);
                    return;
                }
                Terminer("ERREUR", imagesEssayees
                    ? "Ni yt-dlp (vidéo et son) ni gallery-dl (images) n'ont pu lire cette adresse, et elle ne mène pas à un simple fichier. Le détail est dans le journal. "
                      + "Si le site a changé, essayez le bouton Tout mettre à jour."
                    : $"yt-dlp a signalé une erreur (code {code}), le détail est dans le journal. "
                      + "Si le site a changé, essayez le bouton Tout mettre à jour.", Ton.Erreur);
            }
        });
    }

    /// <summary>
    /// Met à jour les trois outils, l'un après l'autre : yt-dlp par sa propre commande, puis ffmpeg et Deno
    /// depuis leurs dépôts officiels. Un outil qui échoue n'empêche pas les suivants.
    /// </summary>
    private async void MettreAJour(object sender, RoutedEventArgs e)
    {
        await Travailler(annulable: false, async () =>
        {
            ViderLaZoneDEtat();
            var executable = await VerifierYtDlpAsync();
            if (executable is null) return;

            _miseAJour = true;
            _annulationDemandee = false;
            var echecs = new List<string>();
            var annule = false;

            // yt-dlp. Pas de bouton Annuler ici, l'interrompre pendant qu'il remplace son propre fichier le laisserait inutilisable.
            Ecrire("");
            Ecrire("Recherche d'une version plus récente de yt-dlp…");
            if (await LancerAsync(executable, ["-U"], "Mise à jour de yt-dlp…") != 0) echecs.Add("yt-dlp");
            // Courte attente par prudence, le temps que le remplacement du fichier soit bien achevé sur le disque.
            await Task.Delay(1500);

            // ffmpeg puis Deno. Ces téléchargements-là peuvent être annulés tant que rien n'est remplacé.
            using var arret = _arretMiseAJour = new CancellationTokenSource();
            BoutonAnnuler.Visibility = Visibility.Visible;
            Mesures.Visibility = Visibility.Visible;
            BarreProgression.Visibility = Visibility.Visible;
            MesureLiaison.Text = "HTTPS direct";
            MesureFormat.Text = "archive";
            try
            {
                using var client = MiseAJourDesOutils.NouveauClient();
#if PORTABLE
                // Sous Linux ffmpeg et aria2 suivent les mises à jour du système, il n'y a que Deno et gallery-dl à voir ici.
                (string Nom, Func<IProgress<MiseAJourDesOutils.Avancement>, Task<string>> Tache)[] outils = Plateforme.Windows
                    ?
                    [
                        ("ffmpeg", p => MiseAJourDesOutils.FfmpegAsync(client, p, arret.Token)),
                        ("Deno", p => MiseAJourDesOutils.DenoAsync(client, p, arret.Token)),
                        ("spotDL", p => MiseAJourDesOutils.SpotDlAsync(client, p, arret.Token)),
                        ("gallery-dl", p => MiseAJourDesOutils.GalleryDlAsync(client, p, arret.Token)),
                    ]
                    :
                    [
                        ("Deno", p => MiseAJourDesOutils.DenoLinuxAsync(client, p, arret.Token)),
                        ("gallery-dl", p => MiseAJourDesOutils.GalleryDlAsync(client, p, arret.Token)),
                    ];
#else
                (string Nom, Func<IProgress<MiseAJourDesOutils.Avancement>, Task<string>> Tache)[] outils =
                [
                    ("ffmpeg", p => MiseAJourDesOutils.FfmpegAsync(client, p, arret.Token)),
                    ("Deno", p => MiseAJourDesOutils.DenoAsync(client, p, arret.Token)),
                    ("spotDL", p => MiseAJourDesOutils.SpotDlAsync(client, p, arret.Token)),
                    ("gallery-dl", p => MiseAJourDesOutils.GalleryDlAsync(client, p, arret.Token)),
                ];
#endif
                foreach (var (nom, tache) in outils)
                {
                    Etat($"Recherche de la dernière version de {nom}…", annoncer: true);
                    BarreProgression.IsIndeterminate = true;
                    var chrono = Stopwatch.StartNew();
                    var avancement = new Progress<MiseAJourDesOutils.Avancement>(a => MontrerUnTransfert(a, chrono.Elapsed.TotalSeconds));
                    try
                    {
                        Ecrire(await tache(avancement));
                    }
                    catch (OperationCanceledException)
                    {
                        annule = true;
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                                   or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        echecs.Add(nom);
                        Ecrire($"ERROR: {nom} n'a pas pu être mis à jour. {ex.Message}");
                    }
                }
            }
            finally
            {
                _arretMiseAJour = null;
                BoutonAnnuler.Visibility = Visibility.Collapsed;
                await Task.Run(MiseAJourDesOutils.Nettoyer);
            }

            BarreProgression.IsIndeterminate = false;
            BarreProgression.Visibility = Visibility.Collapsed;
            Mesures.Visibility = Visibility.Collapsed;
            await VerifierYtDlpAsync();
            // L'application elle-même ne se remplace pas ici, mais c'est le bon moment pour regarder si elle a une suite.
            await ChercherUneNouvelleVersionAsync();
            var suite = _nouvelleVersion is { } publiee
                ? $" Une version {VersionDeLApplication.Lisible(publiee.Numero)} de l'application est aussi disponible, le bouton est en haut à droite."
                : "";

            if (annule)
            {
                Terminer("ANNULÉ", "Mise à jour interrompue. Les outils déjà traités restent à jour, les autres sont inchangés.", Ton.Neutre);
            }
            else if (echecs.Count == 0)
            {
#if PORTABLE
                Terminer("À JOUR", (Plateforme.Windows
                    ? "yt-dlp, ffmpeg, Deno, spotDL et gallery-dl sont à leur dernière version."
                    : "yt-dlp, Deno et gallery-dl sont à leur dernière version. ffmpeg et aria2 suivent les mises à jour du système.") + suite, Ton.Reussite);
#else
                Terminer("À JOUR", "yt-dlp, ffmpeg, Deno, spotDL et gallery-dl sont à leur dernière version." + suite, Ton.Reussite);
#endif
            }
            else
            {
                BasculeJournal.IsChecked = true;
                Terminer("ERREUR", $"La mise à jour a échoué pour {string.Join(" et ", echecs)}. Le détail est dans le journal.", Ton.Erreur);
            }
        });
    }

    /// <summary>Mesures d'un téléchargement fait par l'application elle-même, pendant une mise à jour.</summary>
    private void MontrerUnTransfert(MiseAJourDesOutils.Avancement a, double secondes)
    {
        var debit = secondes > 0.5 ? a.Recu / secondes : 0;
        BarreProgression.IsIndeterminate = a.Total <= 0;
        if (a.Total > 0)
        {
            BarreProgression.Value = 100.0 * a.Recu / a.Total;
            Afficher($"{Math.Floor(100.0 * a.Recu / a.Total):0} %", Ton.Neutre);
        }
        MesureDebit.Text = debit > 0 ? $"{Octets(debit)}/s" : "—";
        MesureRecu.Text = a.Total > 0 ? $"{Octets(a.Recu)} / {Octets(a.Total)}" : Octets(a.Recu);
        MesureReste.Text = debit > 0 && a.Total > a.Recu ? Duree((a.Total - a.Recu) / debit) : "—";
        MesureEcoule.Text = Duree(secondes);
        Etat(a.Message);
    }

    private async void Annuler(object sender, RoutedEventArgs e)
    {
        // Pendant une mise à jour, Annuler n'interrompt que les téléchargements de ffmpeg et de Deno.
        if (_arretMiseAJour is { } arret)
        {
            BoutonAnnuler.IsEnabled = false;
            arret.Cancel();
            return;
        }

        if (_miseAJour || _annulationDemandee) return;
        _annulationDemandee = true;
        BoutonAnnuler.IsEnabled = false;
        Etat("Annulation…", annoncer: true);
        // Un fichier rapatrié par l'application elle-même s'interrompt par son propre signal, il n'y a pas d'outil à arrêter.
        _arretDuTransfert?.Cancel();
        // Tous les yt-dlp en marche sont arrêtés, celui d'une vidéo seule comme les cinq d'une liste.
        // S'il n'y en a aucun (lecture d'un sommaire), la demande sera lue juste avant le lancement suivant.
        var enMarche = _actifs.Select(t => t.Processus).Append(_enCours).Append(_lectureEnFond).OfType<Process>().ToList();
        if (enMarche.Count == 0) return;
        // L'arrêt attend quelques secondes que chaque yt-dlp fasse son ménage, hors du fil de la fenêtre pour ne pas la figer.
        await Task.Run(() => Parallel.ForEach(enMarche, processus => ArbreDeProcessus.Arreter(processus)));
    }

    private void FermerLaFenetre(object? sender, CancelEventArgs e)
    {
        if (_miseAJour)
        {
            e.Cancel = true;
            Etat("La mise à jour se termine, la fenêtre pourra être fermée dans un instant. Le bouton Annuler l'interrompt proprement.", annoncer: true);
            return;
        }
        _annulationDemandee = true;   // plus aucun travail de liste ne démarre pendant la fermeture
        _arretDuTransfert?.Cancel();
        // Les torrents s'arrêtent avec l'application. Ce qui est reçu reste sur le disque, les rajouter les reprendra.
        _vueTorrents?.Arreter();
        var enMarche = _actifs.Select(t => t.Processus).Append(_enCours).Append(_lectureEnFond).OfType<Process>().ToList();
        Parallel.ForEach(enMarche, processus => ArbreDeProcessus.Arreter(processus, delaiDeMenageMs: 1500));
    }

    private Task<int> LancerAsync(string executable, IEnumerable<string> arguments, string etat) =>
        LancerAsync(YtDlp.Preparer(executable, arguments), etat, RecevoirLigne);

    /// <summary>
    /// Lance un outil (yt-dlp ou spotDL), confie chacune de ses lignes au lecteur donné, au fil de l'eau,
    /// et rend son code de sortie.
    /// </summary>
    private async Task<int> LancerAsync(ProcessStartInfo demarrage, string etat, Action<string> lecteur)
    {
        Etat(etat, annoncer: true);
        BarreProgression.ClearValue(ForegroundProperty);   // retour à la couleur de travail du style
        BarreProgression.Visibility = Visibility.Visible;
        // Au milieu d'une liste, l'afficheur et la barre gardent l'avancement de l'ensemble d'un lancement à l'autre.
        if (_total <= 1)
        {
            Afficher("", Ton.Neutre);
            BarreProgression.Value = 0;
            BarreProgression.IsIndeterminate = true;       // un segment parcourt la barre tant qu'aucun pourcentage n'est connu
            BarreDesTaches.ProgressState = TaskbarItemProgressState.Indeterminate;
        }

        try
        {
            using var processus = new Process { StartInfo = demarrage };
            processus.OutputDataReceived += (_, e) => Transmettre(e.Data, lecteur);
            processus.ErrorDataReceived += (_, e) => Transmettre(e.Data, lecteur);
            processus.Start();
            _enCours = processus;
            processus.BeginOutputReadLine();
            processus.BeginErrorReadLine();
            await processus.WaitForExitAsync();
            return processus.ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Ecrire($"ERROR: impossible de lancer {Path.GetFileName(demarrage.FileName)}. {e.Message}");
            return -1;
        }
        finally
        {
            _enCours = null;
            BarreProgression.IsIndeterminate = false;
            BarreDesTaches.ProgressState = TaskbarItemProgressState.None;
        }
    }

    /// <summary>Les lignes arrivent d'un autre fil d'exécution, elles sont renvoyées vers celui de la fenêtre.</summary>
    private void Transmettre(string? ligne, Action<string> lecteur)
    {
        if (ligne is null) return;
        Dispatcher.BeginInvoke(() => lecteur(ligne));
    }

    // ───────────────────────────── Listes menées à plusieurs de front ─────────────────────────────

    // Nombre de téléchargements menés en même temps dans un album, une playlist ou une file de liens.
    // Réglable de 1 à 5 dans la fenêtre, cinq par défaut. Au-delà, YouTube refuse la connexion.
    private const int SimultanesAuPlus = 5;
    private int Simultanes => ChoixSimultanes.SelectedItem is int nombre ? Math.Min(nombre, SimultanesAuPlus) : SimultanesAuPlus;

    // Qualité du son choisie au moment où l'on clique Télécharger, gardée telle quelle pour tout le travail.
    private string _qualiteDuSon = "0";

    // Filtres de l'égaliseur choisi au clic, vides pour aucun. Il ne s'applique qu'au son seul.
    private string _egaliseur = "";

    /// <summary>Un téléchargement d'une liste : sa ligne à l'écran, et les recherches à essayer dans l'ordre.</summary>
    private sealed class Travail(ElementDePlaylist element, string enCasDEchec, params Func<string, List<string>>[] tentatives)
    {
        public ElementDePlaylist Element { get; } = element;

        /// <summary>Le mot affiché si aucune tentative n'aboutit, « erreur » ou « introuvable ».</summary>
        public string EnCasDEchec { get; } = enCasDEchec;

        /// <summary>Chaque tentative reçoit son fichier de suivi et rend les arguments de yt-dlp. La première qui donne un fichier gagne.</summary>
        public Func<string, List<string>>[] Tentatives { get; } = tentatives;

        /// <summary>
        /// Cherche dans le dossier le fichier que ce travail produirait, quand son nom est connu d'avance.
        /// S'il y est déjà, il n'y a rien à faire. La question est posée au dernier moment, juste avant de lancer.
        /// </summary>
        public Func<string?>? ChercherExistant { get; init; }

        /// <summary>
        /// Le chemin du fichier attendu, quand il est connu d'avance (un morceau). Il sert à retirer la jaquette
        /// que yt-dlp pose à côté avant le transfert, et qui resterait seule si le transfert échoue.
        /// </summary>
        public string? CheminAttendu { get; init; }

        /// <summary>
        /// Dernier recours quand aucune tentative de yt-dlp n'a rien donné : les arguments de gallery-dl pour
        /// chercher des images à la même adresse. Absent pour un morceau de musique ou en mode Audio.
        /// </summary>
        public Func<List<string>>? RecoursImages { get; init; }

        /// <summary>
        /// Pour un document ou une archive : le téléchargement direct, fait par l'application elle-même, sans outil.
        /// Il rend le chemin du fichier reçu, ou null quand l'adresse ne sert pas un fichier.
        /// </summary>
        public Func<Travail, Task<string?>>? Direct { get; init; }

        /// <summary>Vrai dès que ce travail a donné son fichier. Les autres sont proposés à la reprise.</summary>
        public bool Reussi;

        /// <summary>Pour un morceau de musique : le titre que le catalogue lui donne, à comparer à celui de la vidéo retenue.</summary>
        public string? TitreAttendu { get; init; }

        /// <summary>Le titre de la vidéo que yt-dlp est en train de prendre pour ce travail.</summary>
        public string TitreTrouve = "";

        /// <summary>Vrai quand YouTube a refusé la connexion pendant la tentative en cours. Le travail retourne alors dans la file.</summary>
        public bool Refuse;

        /// <summary>Les refus qui tiennent à ce travail lui-même : d'autres sont passés entre-temps, sans blocage de la connexion.</summary>
        public int RefusIsoles;

        /// <summary>Le nombre de réussites de la liste au moment du refus précédent de ce travail, pour savoir si d'autres sont passés depuis.</summary>
        public int ReussitesAuDernierRefus = -1;

        public Process? Processus;
        public double Recu, Total, Debit;   // le fichier en cours de transfert
        public double DejaRecu;             // les fichiers déjà finis de ce travail, l'image avant le son par exemple
        public bool DejaLa;
        public string Liaison = "—", Format = "—";
    }

    // Blocage de YouTube. Après beaucoup de demandes rapprochées, YouTube refuse ce qui vient de la même connexion.
    // Deux formes : « Sign in to confirm you're not a bot », relevée ici sur une longue playlist, et « rate-limited
    // by YouTube for up to an hour », écrite dans le code de yt-dlp. Insister ne sert à rien et prolonge le refus.
    // Une liste ne s'arrête plus pour autant. Elle se met en pause, attend, envoie un seul morceau en éclaireur,
    // puis repart au ralenti. Un morceau refusé retourne dans la file, il n'est pas « introuvable ».
    private const string MarqueDuBlocage = "Sign in to confirm";
    private const string MarqueDeLaLimite = "rate-limited by YouTube";

    /// <summary>Vrai pour une ligne de yt-dlp qui rapporte un refus de la connexion par YouTube.</summary>
    private static bool EstUnRefusDeYoutube(string ligne) =>
        ligne.Contains(MarqueDeLaLimite, StringComparison.Ordinal)
        // « Sign in to confirm your age » désigne une vidéo réservée aux adultes, pas un blocage de la connexion.
        || (ligne.Contains(MarqueDuBlocage, StringComparison.Ordinal) && !ligne.Contains("your age", StringComparison.Ordinal));

    // Il faut plusieurs refus de suite, sans une réussite entre eux, pour parler de blocage. Un refus isolé arrive aussi sans cela.
    private const int RefusAvantPause = 3;

    // Minutes d'attente, de plus en plus longues tant que l'éclaireur est refusé. yt-dlp annonce « jusqu'à une heure ».
    // Au bout de la série, près de quatre heures, la liste rend la main.
    private static readonly int[] AttentesApresRefus = [5, 15, 30, 60, 60, 60];

    // Une fois repartie, la liste garde cette allure jusqu'au bout. C'est la rafale de demandes qui a provoqué le refus.
    private const int DeFrontApresRefus = 2;
    private const int SouffleApresRefus = 5;   // secondes entre deux départs

    // Un morceau refusé trois fois alors que d'autres passent entre-temps : le refus vient de lui, on n'insiste pas.
    private const int RefusIsolesAvantAbandon = 3;

    private int _refusDeYoutube;
    private bool _youtubeBloque;

    // Pour les essais (variable DOWNLOAD_EVERYTHING_ESSAI_REFUS, « nombre,secondes ») : les premiers lancements d'une
    // liste reçoivent un refus fabriqué au lieu de partir vers YouTube, et les attentes se comptent en secondes.
    // Sans cela il faudrait se faire bloquer pour de bon pour vérifier la pause.
    private int _refusFabriques;
    private int? _attenteDEssai;

    private const string MessageDuBlocage =
        "YouTube refuse pour l'instant les demandes venant de cette connexion, après trop de téléchargements rapprochés. "
        + "Ce n'est pas une panne et les morceaux ne sont pas introuvables. Réessayez dans quelques heures, avec moins de "
        + "téléchargements à la fois (3 par exemple). Ce qui est déjà reçu reste dans le dossier et ne sera pas repris.";

    private const string MessageApresLesAttentes =
        "YouTube refuse toujours les demandes de cette connexion, après six pauses et près de quatre heures d'attente. "
        + "Ce n'est pas une panne et les morceaux ne sont pas introuvables. Ce qui est déjà reçu reste dans le dossier, "
        + "le bouton de reprise relancera le reste plus tard.";

    // Débuts des lignes que yt-dlp écrit pour chaque recherche, sans intérêt dans le journal d'une liste.
    private static readonly string[] BruitDeRecherche =
    [
        "[youtube:music:search_url]", "[youtube:search]", "[download] Downloading playlist:",
        "[download] Finished downloading playlist:", "[download] Downloading item ", "[info] Maximum number of downloads reached",
    ];

    // Les travaux dont un yt-dlp tourne en ce moment. Tout se passe sur le fil de la fenêtre, sans verrou.
    private readonly List<Travail> _actifs = [];

    /// <summary>
    /// Mène une liste de téléchargements, plusieurs à la fois. Chaque « ouvrier » prend le travail suivant dans
    /// la file dès qu'il a fini le sien. Rend les fichiers obtenus et le nombre de travaux restés sans résultat.
    /// </summary>
    /// <param name="suite">
    /// Des travaux qui arriveront plus tard, la fin d'une grande playlist encore en cours de lecture.
    /// Ils rejoignent la file dès qu'ils sont connus, et les ouvriers les attendent s'ils ont fini avant.
    /// </param>
    private async Task<(List<string> Fichiers, int Echecs)> MenerLaListeAsync(
        string executable, List<Travail> travaux, Task<List<Travail>>? suite = null)
    {
        _listeMaitrisee = true;
        _total = travaux.Count;
        var file = new Queue<Travail>(travaux);
        var fichiers = new List<string>();
        var echecs = 0;
        var faits = 0;
        var chrono = Stopwatch.StartNew();
        var deFront = suite is null ? Math.Min(Simultanes, travaux.Count) : Simultanes;
        var suiteVersee = suite is null;

        // Allure de la liste. Elle ne change que si YouTube refuse la connexion, voir MettreEnPause.
        var permis = deFront;                    // combien de travaux peuvent être en route à la fois
        var souffle = TimeSpan.Zero;             // délai entre deux départs, une fois au ralenti
        var prochainDepart = DateTime.MinValue;
        var reussites = 0;
        var refusDeSuite = 0;                    // refus sans une réussite entre eux
        var pausesDeSuite = 0;                   // pauses sans qu'un éclaireur soit passé
        DateTime? reprise = null;                // l'heure où la pause en cours finit
        var eclaireurAttendu = false;            // après une pause, un seul travail part d'abord
        Travail? eclaireur = null;
        var essai = (Environment.GetEnvironmentVariable("DOWNLOAD_EVERYTHING_ESSAI_REFUS") ?? "").Split(',');
        _refusFabriques = int.TryParse(essai[0], out var fabriques) ? fabriques : 0;
        _attenteDEssai = essai.Length > 1 && int.TryParse(essai[1], out var secondesDEssai) ? secondesDEssai : null;

        Mesures.Visibility = Visibility.Visible;
        BarreProgression.ClearValue(ForegroundProperty);
        BarreProgression.Value = 0;
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;
        BarreDesTaches.ProgressState = TaskbarItemProgressState.Indeterminate;
        if (_total > 1) BasculeElements.Visibility = Visibility.Visible;
        Etat(suite is not null ? $"{_total} téléchargements, {deFront} à la fois. Lecture de la suite de la playlist en cours…"
            : _total > 1 ? $"{_total} téléchargements, {deFront} à la fois"
            : "Recherche…", annoncer: true);

        void Montrer() => MontrerLEnsemble(travaux, faits, chrono.Elapsed.TotalSeconds);

        // La suite de la playlist vient d'être lue : ses travaux rejoignent la file et le total affiché grandit.
        void VerserLaSuite()
        {
            suiteVersee = true;
            foreach (var travail in suite!.IsCompletedSuccessfully ? suite.Result : [])
            {
                travaux.Add(travail);
                file.Enqueue(travail);
            }
            _total = travaux.Count;
            _totalAnnonce = 0;   // la file contient maintenant tout, c'est elle qui fait foi
            Etat($"{_total} téléchargements, {deFront} à la fois", annoncer: true);
            Montrer();
        }

        // Le décompte de la pause, rafraîchi par les ouvriers qui attendent.
        void MontrerLaPause(bool annoncer = false)
        {
            var reste = (reprise!.Value - DateTime.Now).TotalSeconds;
            Etat($"YouTube refuse les demandes de cette connexion. Pause jusqu'à {reprise.Value:HH\\hmm}, encore {Duree(reste)}. "
                 + "La liste reprendra seule, au ralenti. Rien n'est perdu.", Ton.Neutre, annoncer);
            Montrer();
            // Après Montrer, qui remet l'icône de la barre des tâches en marche normale : pendant la pause elle reste en jaune.
            BarreDesTaches.ProgressState = TaskbarItemProgressState.Paused;
        }

        // YouTube refuse la connexion. Plus rien ne part jusqu'à l'heure de reprise, puis un seul travail, en éclaireur.
        // Les yt-dlp déjà en route ne sont pas arrêtés : un transfert commencé va au bout, un refus revient en quelques secondes.
        void MettreEnPause()
        {
            if (reprise is not null || _annulationDemandee) return;
            if (pausesDeSuite >= AttentesApresRefus.Length)
            {
                // Toute la série d'attentes est passée sans qu'un seul éclaireur aboutisse : la liste rend la main.
                _youtubeBloque = true;
                _annulationDemandee = true;
                foreach (var enFile in file) enFile.Element.Etat = "refusé";
                if (_lectureEnFond is { } lecture) _ = Task.Run(() => ArbreDeProcessus.Arreter(lecture));
                return;
            }

            var attente = _attenteDEssai is { } secondes
                ? TimeSpan.FromSeconds(secondes)
                : TimeSpan.FromMinutes(AttentesApresRefus[pausesDeSuite]);
            pausesDeSuite++;
            reprise = DateTime.Now + attente;
            eclaireurAttendu = true;
            permis = 1;
            // Un blocage reconnu : les refus qui l'entourent ne tiennent pas aux morceaux eux-mêmes.
            foreach (var enFile in file) enFile.RefusIsoles = 0;
            var duree = attente.TotalMinutes >= 1 ? $"{attente.TotalMinutes:0} minutes" : $"{attente.TotalSeconds:0} secondes";
            Ecrire($"YouTube refuse les demandes de cette connexion. Pause de {duree}, reprise à {reprise.Value:HH\\hmm}. "
                   + "Les morceaux refusés retournent dans la file, ils seront repris.");
            MontrerLaPause(annoncer: true);
        }

        async Task Ouvrier(int numero)
        {
            // Un fichier de suivi par ouvrier, sinon cinq yt-dlp écriraient leur résultat au même endroit.
            var suivi = Path.Combine(YtDlp.DossierOutils, $"suivi-{numero}.txt");
            while (!_annulationDemandee)
            {
                if (file.Count == 0)
                {
                    if (suiteVersee)
                    {
                        // Plus rien dans la file. Mais un travail encore en route y reviendra si YouTube le refuse,
                        // on ne part donc qu'une fois tous les travaux rentrés.
                        if (_actifs.Count == 0) break;
                        await Task.Delay(300);
                        continue;
                    }
                    // La suite de la playlist est encore en lecture, on l'attend.
                    await Task.WhenAny(suite!, Task.Delay(500));
                    if (suite!.IsCompleted && !suiteVersee) VerserLaSuite();
                    // Le temps écoulé continue d'avancer pendant l'attente, sinon l'affichage semblerait figé.
                    else if (numero == 1) Montrer();
                    continue;
                }

                if (reprise is { } heure)
                {
                    if (DateTime.Now < heure)
                    {
                        MontrerLaPause();
                        await Task.Delay(500);
                        continue;
                    }
                    reprise = null;
                    BarreDesTaches.ProgressState = TaskbarItemProgressState.Normal;
                    Ecrire("Fin de la pause. Un seul morceau part d'abord, pour voir si YouTube répond de nouveau.");
                    Etat("Fin de la pause, essai d'un seul morceau…", annoncer: true);
                }
                // Jamais plus de travaux en route que permis, et un souffle entre deux départs quand la liste est au ralenti.
                if (_actifs.Count >= permis || DateTime.Now < prochainDepart)
                {
                    await Task.Delay(300);
                    continue;
                }

                var travail = file.Dequeue();
                if (travail.ChercherExistant?.Invoke() is { } existant)
                {
                    // Déjà dans le dossier, d'un téléchargement précédent. Rien à lancer.
                    fichiers.Add(existant);
                    travail.Reussi = true;
                    travail.Element.Etat = "déjà là";
                    travail.Element.Pourcent = 100;
                    faits++;
                    Montrer();
                    continue;
                }
                _actifs.Add(travail);
                if (eclaireurAttendu)
                {
                    eclaireur = travail;
                    eclaireurAttendu = false;
                }
                if (souffle > TimeSpan.Zero) prochainDepart = DateTime.Now + souffle;
                travail.Refuse = false;
                travail.Element.Etat = "recherche";
                Montrer();

                string? fichier = null;
                if (travail.Direct is { } direct)
                {
                    travail.Element.Etat = "en cours";
                    try
                    {
                        fichier = await direct(travail);
                    }
                    catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                                  or InvalidOperationException or OperationCanceledException)
                    {
                        if (!_annulationDemandee) Ecrire($"ERROR: {e.Message}", travail.Element.Numero);
                    }
                }
                foreach (var tentative in travail.Tentatives)
                {
                    if (_annulationDemandee) break;
                    YtDlp.EffacerLaTrace(suivi);
                    travail.DejaLa = false;
                    travail.TitreTrouve = "";
                    await LancerUnTravailAsync(executable, tentative(suivi), travail, Montrer);
                    if (YtDlp.LireLaTrace(suivi) is { Count: > 0 } obtenus)
                    {
                        fichier = obtenus[^1];
                        break;
                    }
                    // Refusé par YouTube : la tentative suivante le serait aussi, inutile d'envoyer une demande de plus.
                    if (travail.Refuse) break;
                }

                // Dernier recours pour une adresse où yt-dlp n'a rien trouvé : des images, par gallery-dl.
                // Pas après un refus de YouTube, où il n'y a rien à chercher ailleurs.
                if (fichier is null && !_annulationDemandee && !travail.Refuse && travail.RecoursImages is { } recours && GalleryDl.Trouver() is { } galerie)
                {
                    var images = await LancerLeRecoursImagesAsync(galerie, recours(), travail);
                    if (images.Count > 0)
                    {
                        fichiers.AddRange(images.Take(images.Count - 1));
                        fichier = images[^1];
                    }
                }

                // Niveau égalisé : le morceau vient d'arriver, il est mesuré puis ramené vers le niveau des morceaux de club.
                // C'est fait avant de rendre la place dans la file, pour qu'une annulation arrête aussi cette étape.
                if (fichier is not null && _niveau && !travail.DejaLa && !_annulationDemandee)
                {
                    travail.Element.Etat = "niveau";
                    Montrer();
                    await EgaliserLeNiveauAsync(fichier, travail.Element.Numero, processus => travail.Processus = processus);
                }

                YtDlp.EffacerLaTrace(suivi);
                _actifs.Remove(travail);
                travail.Recu = travail.Total = travail.Debit = 0;
                // Rien d'obtenu : la jaquette que yt-dlp a pu poser avant le transfert ne reste pas seule dans le dossier.
                if (fichier is null) YtDlp.EffacerLaJaquetteOrpheline(travail.CheminAttendu);

                var refuse = fichier is null && travail.Refuse && !_annulationDemandee;
                var etaitLEclaireur = travail == eclaireur;
                if (etaitLEclaireur) eclaireur = null;
                if (etaitLEclaireur && !refuse && !_annulationDemandee)
                {
                    // L'éclaireur est revenu sans refus, trouvé ou non : YouTube répond. La liste repart, moins vite qu'avant.
                    pausesDeSuite = 0;
                    permis = Math.Min(deFront, DeFrontApresRefus);
                    souffle = TimeSpan.FromSeconds(_attenteDEssai is null ? SouffleApresRefus : 1);
                    prochainDepart = DateTime.Now + souffle;
                    var allure = permis > 1 ? $"{permis} à la fois" : "un à la fois";
                    Ecrire($"YouTube répond de nouveau. La liste repart au ralenti, {allure}, avec {souffle.TotalSeconds:0} secondes entre deux départs.");
                    Etat($"{_total} téléchargements, au ralenti après un refus de YouTube, {allure}", annoncer: true);
                }

                if (fichier is not null)
                {
                    fichiers.Add(fichier);
                    travail.Reussi = true;
                    reussites++;
                    refusDeSuite = 0;
                    // Garde-fou : le morceau est arrivé, mais la vidéo retenue porte-t-elle bien son titre ?
                    var doute = travail.DejaLa || travail.TitreAttendu is null ? null : YtDlp.DouteSurLeTitre(travail.TitreAttendu, travail.TitreTrouve);
                    if (doute is not null)
                    {
                        _aVerifier.Add((travail.Element.Numero, travail.Element.Titre, travail.TitreTrouve, doute));
                        Ecrire($"À vérifier, {travail.Element.Titre}. La vidéo retenue s'appelle « {travail.TitreTrouve} », {doute}.", travail.Element.Numero);
                    }
                    travail.Element.Etat = travail.DejaLa ? "déjà là" : doute is null ? "terminé" : "à vérifier";
                    travail.Element.Pourcent = 100;
                    faits++;
                }
                else if (_annulationDemandee)
                {
                    // Arrêté par l'utilisateur, ou par le blocage de YouTube. Dans les deux cas le morceau n'est pas « introuvable ».
                    travail.Element.Etat = _youtubeBloque ? "refusé" : "annulé";
                }
                else if (refuse)
                {
                    // Un refus ne compte contre le morceau lui-même que hors blocage, et si d'autres sont passés depuis son refus précédent.
                    if (reprise is null && reussites > travail.ReussitesAuDernierRefus) travail.RefusIsoles++;
                    travail.ReussitesAuDernierRefus = reussites;
                    if (travail.RefusIsoles >= RefusIsolesAvantAbandon)
                    {
                        travail.Element.Etat = "refusé";
                        echecs++;
                        faits++;
                        Ecrire($"ERROR: {travail.Element.Titre} est refusé par YouTube alors que les autres passent. Il est laissé de côté.", travail.Element.Numero);
                    }
                    else
                    {
                        // Refusé, pas introuvable : il retourne en fin de file et sera repris.
                        travail.Element.Etat = "en attente";
                        travail.Element.Pourcent = 0;
                        file.Enqueue(travail);
                        refusDeSuite++;
                        // Rien ne repart pendant deux secondes : les autres travaux en route reviennent, et l'on sait
                        // si c'est un blocage avant d'avoir envoyé d'autres demandes pour rien.
                        if (prochainDepart < DateTime.Now.AddSeconds(2)) prochainDepart = DateTime.Now.AddSeconds(2);
                        // Après une pause, le refus du seul éclaireur suffit à dire que le blocage dure. Sinon il en faut plusieurs de suite.
                        if (etaitLEclaireur || refusDeSuite >= Math.Min(RefusAvantPause, travaux.Count)) MettreEnPause();
                    }
                }
                else
                {
                    travail.Element.Etat = travail.EnCasDEchec;
                    echecs++;
                    faits++;
                    Ecrire($"ERROR: {travail.Element.Titre} n'a pas pu être téléchargé.", travail.Element.Numero);
                }
                Montrer();
            }
        }

        await Task.WhenAll(Enumerable.Range(1, deFront).Select(Ouvrier));
        BarreDesTaches.ProgressState = TaskbarItemProgressState.None;
        // Tout ce qui n'a pas abouti peut être repris d'un clic : les introuvables et les erreurs, mais aussi
        // ce qu'une annulation ou un blocage de YouTube a laissé en route ou n'a jamais laissé commencer.
        _aReprendre = travaux.Where(t => !t.Reussi).ToList();
        return (fichiers, echecs);
    }

    /// <summary>
    /// Fait chercher des images par gallery-dl pour un travail de la liste. Rend les fichiers obtenus,
    /// ceux qui étaient déjà dans le dossier compris.
    /// </summary>
    private async Task<List<string>> LancerLeRecoursImagesAsync(string galerie, List<string> arguments, Travail travail)
    {
        var recus = new List<string>();
        var dejaLa = 0;
        void Lire(string ligne)
        {
            var (fichier, deja) = GalleryDl.LireUneLigne(ligne);
            if (fichier is null)
            {
                Ecrire(ligne, travail.Element.Numero);
                return;
            }
            if (recus.Contains(fichier)) return;
            recus.Add(fichier);
            if (deja) dejaLa++;
            travail.Element.Etat = recus.Count > 1 ? $"{recus.Count} images" : "1 image";
        }

        Ecrire("yt-dlp n'a rien trouvé à cette adresse, recherche d'images par gallery-dl.", travail.Element.Numero);
        travail.Element.Etat = "images";
        try
        {
            using var processus = new Process { StartInfo = GalleryDl.Preparer(galerie, arguments) };
            processus.OutputDataReceived += (_, e) => Transmettre(e.Data, Lire);
            processus.ErrorDataReceived += (_, e) => Transmettre(e.Data, Lire);
            processus.Start();
            travail.Processus = processus;
            processus.BeginOutputReadLine();
            processus.BeginErrorReadLine();
            await processus.WaitForExitAsync();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Ecrire($"ERROR: impossible de lancer gallery-dl. {e.Message}", travail.Element.Numero);
        }
        finally
        {
            travail.Processus = null;
        }
        // Les dernières lignes attendent encore leur tour sur le fil de la fenêtre, on les laisse passer avant de compter.
        await Dispatcher.Yield(DispatcherPriority.Background);
        travail.DejaLa = recus.Count > 0 && dejaLa == recus.Count;
        return recus;
    }

    private async Task LancerUnTravailAsync(string executable, List<string> arguments, Travail travail, Action montrer)
    {
        travail.Recu = travail.Total = travail.Debit = 0;
        void Lire(string ligne)
        {
            // Seules les lignes de mesure changent les chiffres d'ensemble, inutile de les recalculer pour les autres.
            if (RecevoirLigneDUnTravail(travail, ligne)) montrer();
        }

        if (_refusFabriques > 0)
        {
            // Essai de la pause : ce lancement ne part pas, il reçoit la ligne que yt-dlp écrit quand YouTube refuse.
            _refusFabriques--;
            await Task.Delay(400);
            Lire("ERROR: [youtube] essai: Sign in to confirm you’re not a bot. (refus fabriqué pour l'essai)");
            return;
        }

        try
        {
            using var processus = new Process { StartInfo = YtDlp.Preparer(executable, arguments) };
            processus.OutputDataReceived += (_, e) => Transmettre(e.Data, Lire);
            processus.ErrorDataReceived += (_, e) => Transmettre(e.Data, Lire);
            processus.Start();
            travail.Processus = processus;
            processus.BeginOutputReadLine();
            processus.BeginErrorReadLine();
            await processus.WaitForExitAsync();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Ecrire($"ERROR: impossible de lancer yt-dlp. {e.Message}", travail.Element.Numero);
        }
        finally
        {
            travail.Processus = null;
        }
    }

    /// <summary>Une ligne de yt-dlp pour un travail de la liste. Rend vrai quand c'était une ligne de mesure.</summary>
    private bool RecevoirLigneDUnTravail(Travail travail, string ligne)
    {
        if (ligne.StartsWith(YtDlp.MarqueDeMesure, StringComparison.Ordinal))
        {
            var champs = ligne.Split('|', YtDlp.NombreDeChampsDeMesure);
            if (champs.Length < YtDlp.NombreDeChampsDeMesure) return false;
            var recu = Nombre(champs[2]) ?? 0;
            var total = Nombre(champs[3]) ?? Nombre(champs[4]) ?? 0;
            if (champs[12] != "NA") travail.TitreTrouve = champs[12];
            // Dans une file de liens, la ligne affichait l'adresse en attendant. Voici le vrai titre.
            if (champs[12] != "NA" && travail.Element.Titre.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                travail.Element.Titre = champs[12];
            travail.Liaison = Liaison(champs[10], "NA", "NA");
            travail.Format = champs[11] switch
            {
                "NA" => "—",
                "audio only" => "son seul",
                var definition => definition.Replace("x", " × "),
            };

            if (champs[1] == "finished")
            {
                // Ce fichier est arrivé. Un autre peut suivre pour le même travail, le son après l'image.
                travail.DejaRecu += recu;
                travail.Recu = travail.Total = travail.Debit = 0;
            }
            else
            {
                travail.Recu = recu;
                travail.Total = total;
                travail.Debit = Nombre(champs[5]) ?? 0;
                if (total > 0)
                {
                    travail.Element.Pourcent = Math.Clamp(100 * recu / total, 0, 100);
                    travail.Element.Etat = $"{Math.Floor(travail.Element.Pourcent):0} %";
                }
            }
            return true;
        }

        if (EstUnRefusDeYoutube(ligne))
        {
            travail.Refuse = true;
            _refusDeYoutube++;
        }
        if (ligne.EndsWith("has already been downloaded", StringComparison.Ordinal)) travail.DejaLa = true;
        else if (EtapesDeFfmpeg.Any(etape => ligne.StartsWith(etape, StringComparison.Ordinal))) travail.Element.Etat = "assemblage";

        // Chaque recherche de morceau fait écrire à yt-dlp une demi-douzaine de lignes sur sa « playlist » de
        // résultats. Elles n'apprennent rien et noieraient le journal d'une longue liste, on ne les recopie pas.
        if (BruitDeRecherche.Any(debut => ligne.StartsWith(debut, StringComparison.Ordinal))) return false;
        Ecrire(ligne, travail.Element.Numero);
        return false;
    }

    /// <summary>Les chiffres d'ensemble d'une liste : ce qui est fini, la somme des débits, le temps qui reste.</summary>
    private void MontrerLEnsemble(List<Travail> travaux, int faits, double secondes)
    {
        // Le total est celui de la playlist entière dès qu'il est connu, même si la suite n'a pas encore rejoint la file.
        var total = Math.Max(travaux.Count, _totalAnnonce);
        var enCours = _actifs.Sum(t => t.Total > 0 ? Math.Clamp(t.Recu / t.Total, 0, 1) : 0);
        var part = total > 0 ? Math.Clamp((faits + enCours) / total, 0, 1) : 0;
        if (part > 0) Avancer(part * 100);

        if (total > 1) Afficher($"{faits} / {total}", Ton.Neutre);
        else if (enCours > 0)
        {
            Afficher($"{Math.Floor(enCours * 100):0} %", Ton.Neutre);
            if (TexteEtat.Text == "Recherche…") Etat("");
        }

        var debit = _actifs.Sum(t => t.Debit);
        var recu = travaux.Sum(t => t.DejaRecu + t.Recu);
        MesureDebit.Text = debit > 0 ? $"{Octets(debit)}/s" : "—";
        MesureRecu.Text = recu > 0 ? Octets(recu) : "—";
        MesureEcoule.Text = Duree(secondes);
        // Estimation simple, au rythme tenu depuis le début. Elle n'a de sens qu'une fois la liste un peu entamée.
        MesureReste.Text = part is > 0.03 and < 1 ? Duree(secondes * (1 - part) / part) : "—";
        MesureLiaison.Text = _actifs.Count > 1 ? $"{_actifs.Count} à la fois" : _actifs.FirstOrDefault()?.Liaison ?? MesureLiaison.Text;
        if (_actifs.LastOrDefault(t => t.Format != "—") is { } dernier) MesureFormat.Text = dernier.Format;
    }

    /// <summary>Fin d'une liste, commune aux playlists et aux liens Spotify.</summary>
    private void ConclureLaListe(List<string> fichiers, int echecs)
    {
        if (fichiers.Count > 0)
        {
            _dernierFichier = fichiers[^1];
            var nom = Path.GetFileName(_dernierFichier);
            NomFichier.Text = fichiers.Count == 1 ? nom : $"{fichiers.Count} fichiers, le dernier est {nom}";
            NomFichier.ToolTip = _dernierFichier;
            Derouler(Resultat, true);
        }
        MesureDebit.Text = "—";

        if (_youtubeBloque)
        {
            BarreProgression.IsIndeterminate = false;
            BarreProgression.Visibility = Visibility.Collapsed;
            Terminer("BLOQUÉ", MessageApresLesAttentes, Ton.Erreur);
        }
        else if (_annulationDemandee)
        {
            BarreProgression.IsIndeterminate = false;
            BarreProgression.Visibility = Visibility.Collapsed;
            Terminer("ANNULÉ", fichiers.Count > 0 ? "Ce qui était déjà reçu reste dans le dossier." : "", Ton.Neutre);
        }
        else if (fichiers.Count > 0)
        {
            BarreProgression.IsIndeterminate = false;
            BarreProgression.Value = BarreProgression.Maximum;
            BarreProgression.SetResourceReference(ForegroundProperty, "Reussite");
            MesureReste.Text = "00:00";
            var doutes = _aVerifier.Count == 0 ? ""
                : (_aVerifier.Count > 1
                      ? $"{_aVerifier.Count} morceaux sont à vérifier, la vidéo retenue ne porte pas leur titre. "
                      : "1 morceau est à vérifier, la vidéo retenue ne porte pas son titre. ")
                  + $"Marqués dans la liste et notés dans « {FichierDesDoutes} ».";
            if (echecs > 0)
                Terminer("TERMINÉ", $"{fichiers.Count} {(fichiers.Count > 1 ? "reçus" : "reçu")}, {echecs} en échec. "
                                    + $"Le bouton Reprendre {(echecs > 1 ? "les" : "le")} retente, le détail est dans la liste et dans le journal. " + doutes,
                    Ton.Erreur, motEnVert: true);
            else if (doutes.Length > 0)
                Terminer("TERMINÉ", doutes, Ton.Neutre, motEnVert: true);
            else
                Terminer("TERMINÉ", "", Ton.Reussite);
        }
        else
        {
            BarreProgression.IsIndeterminate = false;
            BarreProgression.Visibility = Visibility.Collapsed;
            Mesures.Visibility = Visibility.Collapsed;
            BasculeJournal.IsChecked = true;
            Terminer("ERREUR", "Rien n'a pu être téléchargé, le détail est dans le journal.", Ton.Erreur);
        }
        ProposerLaReprise();
        NoterLesTitresAVerifier();
    }

    // ───────────────────────────── Morceaux à vérifier ─────────────────────────────

    // Les morceaux arrivés dont la vidéo retenue ne porte pas le titre demandé, depuis la dernière demande.
    private readonly List<(int Numero, string Attendu, string Trouve, string Raison)> _aVerifier = [];

    private const string FichierDesDoutes = "Titres à vérifier.txt";
    private const string EnTeteDesDoutes = "Download Everything, titres à vérifier";

    /// <summary>
    /// Écrit dans le dossier la liste des morceaux à vérifier, avec le titre de la vidéo qui a été prise à leur place
    /// peut-être. Rien n'est supprimé ni refusé : c'est à l'oreille de trancher. Sans doute à noter, le fichier
    /// d'une fois précédente est retiré, s'il est bien le nôtre.
    /// </summary>
    private void NoterLesTitresAVerifier()
    {
        if (_dossierDeLaListe.Length == 0) return;
        var chemin = Path.Combine(_dossierDeLaListe, FichierDesDoutes);
        try
        {
            if (_aVerifier.Count == 0)
            {
                if (File.Exists(chemin) && (File.ReadLines(chemin).FirstOrDefault() ?? "").StartsWith(EnTeteDesDoutes, StringComparison.Ordinal))
                    File.Delete(chemin);
                return;
            }
            var lignes = new List<string>
            {
                $"{EnTeteDesDoutes} du {DateTime.Now.ToString("dd/MM/yyyy", Francais)} à {DateTime.Now.ToString("HH'h'mm", Francais)}",
                "Ces morceaux ont été téléchargés, mais la vidéo retenue ne porte pas le titre demandé.",
                "Écoutez-les : si ce n'est pas le bon morceau, supprimez le fichier et cherchez-le à la main.",
                "",
            };
            foreach (var (numero, attendu, trouve, raison) in _aVerifier)
            {
                lignes.Add($"{numero:000}  {attendu}");
                lignes.Add($"     vidéo retenue : {trouve}  ({raison})");
            }
            File.WriteAllLines(chemin, lignes, System.Text.Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Le fichier n'est qu'un aide-mémoire, la liste à l'écran et le journal disent la même chose.
        }
    }

    // ───────────────────────────── Reprise des manquants ─────────────────────────────

    // Les travaux de la dernière liste qui n'ont pas abouti, et le dossier où elle rangeait ses fichiers.
    private List<Travail> _aReprendre = [];
    private string _dossierDeLaListe = "";

    private const string FichierDesManquants = "Titres manquants.txt";
    private const string EnTeteDesManquants = "Download Everything, titres manquants";

    /// <summary>
    /// Fin d'une liste. S'il reste des travaux sans résultat, le bouton Reprendre apparaît, et les titres
    /// réellement en échec sont notés dans un fichier texte du dossier, pour qui veut les chercher ailleurs.
    /// </summary>
    private void ProposerLaReprise()
    {
        var restants = _aReprendre.Count;
        BoutonReprendre.Content = restants > 1 ? $"_Reprendre les {restants} manquants" : "_Reprendre le manquant";
        BoutonReprendre.Visibility = restants > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Un travail annulé ou refusé par YouTube n'est pas « manquant » au sens du fichier, il n'a pas été cherché jusqu'au bout.
        var enEchec = _aReprendre.Where(t => t.Element.Etat == t.EnCasDEchec).ToList();
        if (_dossierDeLaListe.Length == 0) return;
        var chemin = Path.Combine(_dossierDeLaListe, FichierDesManquants);
        try
        {
            if (enEchec.Count == 0)
            {
                // Plus rien ne manque : le fichier d'une fois précédente n'a plus lieu d'être. On n'efface que le nôtre.
                if (File.Exists(chemin) && (File.ReadLines(chemin).FirstOrDefault() ?? "").StartsWith(EnTeteDesManquants, StringComparison.Ordinal))
                    File.Delete(chemin);
                return;
            }
            var lignes = new List<string>
            {
                $"{EnTeteDesManquants} du {DateTime.Now.ToString("dd/MM/yyyy", Francais)} à {DateTime.Now.ToString("HH'h'mm", Francais)}",
                enEchec.Count > 1
                    ? $"{enEchec.Count} titres n'ont pas pu être téléchargés."
                    : "1 titre n'a pas pu être téléchargé.",
                "",
            };
            lignes.AddRange(enEchec.Select(t => $"{t.Element.Numero:000}  {t.Element.Titre}  ({t.Element.Etat})"));
            File.WriteAllLines(chemin, lignes, System.Text.Encoding.UTF8);
            Ecrire($"La liste des titres manquants est notée dans {chemin}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Le fichier n'est qu'un aide-mémoire, la liste à l'écran et le bouton suffisent.
        }
    }

    /// <summary>
    /// Relance seulement ce qui n'a pas abouti dans la dernière liste, sans relire le lien ni repasser sur ce
    /// qui est déjà reçu. Les réglages du son sont ceux de la demande d'origine, le nombre à la fois est celui du moment.
    /// </summary>
    private async void ReprendreLesManquants(object sender, RoutedEventArgs e)
    {
        if (_occupe || _aReprendre.Count == 0) return;
        var travaux = _aReprendre.ToList();
        await Travailler(annulable: true, async () =>
        {
            ViderLaZoneDEtat();
            BoutonReprendre.Visibility = Visibility.Collapsed;
            var executable = await VerifierYtDlpAsync();
            if (executable is null) return;

            _annulationDemandee = false;
            Ecrire("");
            Ecrire(travaux.Count > 1 ? $"Reprise des {travaux.Count} téléchargements manquants." : "Reprise du téléchargement manquant.");
            foreach (var travail in travaux)
            {
                // Chaque ligne repart de zéro, avec le numéro qu'elle avait dans la liste d'origine.
                travail.Element.Etat = "en attente";
                travail.Element.Pourcent = 0;
                travail.DejaRecu = 0;
                travail.DejaLa = false;
                _elements.Add(travail.Element);
            }
            var (fichiers, echecs) = await MenerLaListeAsync(executable, travaux);
            ConclureLaListe(fichiers, echecs);
        });
    }

    // ───────────────────────────── Images, par gallery-dl ─────────────────────────────

    /// <summary>
    /// Second outil de la cascade pour une adresse seule : yt-dlp n'y a trouvé ni vidéo ni son, gallery-dl y cherche
    /// des images. Rend vrai quand il a conclu lui-même (des images reçues, ou une annulation), faux pour laisser
    /// l'appelant annoncer l'erreur de départ.
    /// </summary>
    private async Task<bool> EssayerLesImagesAsync(string galerie, string adresse, string dossier)
    {
        Ecrire("yt-dlp n'a trouvé ni vidéo ni son à cette adresse. Recherche d'images par gallery-dl.");
        Mesures.Visibility = Visibility.Collapsed;
        var recus = new List<string>();
        var dejaLa = 0;
        void Lire(string ligne)
        {
            var (fichier, deja) = GalleryDl.LireUneLigne(ligne);
            if (fichier is null)
            {
                Ecrire(ligne, enRouge: false, deLOutil: true);
                return;
            }
            if (recus.Contains(fichier)) return;
            recus.Add(fichier);
            if (deja) dejaLa++;
            Etat(recus.Count > 1 ? $"{recus.Count} images reçues…" : "1 image reçue…");
        }

        await LancerAsync(GalleryDl.Preparer(galerie, GalleryDl.Arguments(adresse, dossier)), "Recherche d'images à cette adresse…", Lire);
        // Les dernières lignes attendent encore leur tour sur le fil de la fenêtre, on les laisse passer avant de compter.
        await Dispatcher.Yield(DispatcherPriority.Background);

        if (_annulationDemandee)
        {
            BarreProgression.Visibility = Visibility.Collapsed;
            Terminer("ANNULÉ", recus.Count > 0 ? "Les images déjà reçues restent dans le dossier." : "", Ton.Neutre);
            return true;
        }
        if (recus.Count == 0) return false;

        BarreProgression.IsIndeterminate = false;
        BarreProgression.Value = BarreProgression.Maximum;
        BarreProgression.SetResourceReference(ForegroundProperty, "Reussite");
        _dernierFichier = recus[^1];
        var nom = Path.GetFileName(_dernierFichier);
        NomFichier.Text = recus.Count == 1 ? nom : $"{recus.Count} fichiers, le dernier est {nom}";
        NomFichier.ToolTip = _dernierFichier;
        Derouler(Resultat, true);
        MarquerLElementCourant("terminé");

        // Le sous-dossier seul, au nom du site puis de la galerie. Le chemin complet tiendrait sur deux lignes.
        var rangement = Path.GetRelativePath(dossier, Path.GetDirectoryName(_dernierFichier) ?? dossier);
        var ou = rangement is "." or "" ? "le dossier" : $"le sous-dossier {rangement}";
        if (dejaLa == recus.Count)
            Terminer("DÉJÀ LÀ", recus.Count > 1
                ? $"Ces {recus.Count} images étaient déjà dans {ou}, elles n'ont pas été reprises."
                : $"Cette image était déjà dans {ou}, elle n'a pas été reprise.", Ton.Neutre);
        else
            Terminer("TERMINÉ", recus.Count > 1
                ? $"{recus.Count} images reçues par gallery-dl, rangées dans {ou}."
                : $"1 image reçue par gallery-dl, rangée dans {ou}.", Ton.Reussite);
        return true;
    }

    // ───────────────────────────── Fichier ordinaire, tel quel ─────────────────────────────

    // Le signal d'arrêt du fichier que l'application rapatrie elle-même en ce moment.
    private CancellationTokenSource? _arretDuTransfert;

    /// <summary>Exécute une action en sortant d'un bloc « using », quelle que soit la façon d'en sortir.</summary>
    private sealed class Fin(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>
    /// Dernier étage de la cascade pour une adresse seule : ni vidéo, ni son, ni galerie d'images, mais peut-être
    /// un simple fichier. Rend vrai quand il a conclu lui-même (fichier reçu, ou annulation), faux quand l'adresse
    /// ne sert pas un fichier, pour laisser l'appelant annoncer l'erreur de départ.
    /// </summary>
    private async Task<bool> EssayerLeFichierDirectAsync(string adresse, string dossier)
    {
        using var arret = _arretDuTransfert = new CancellationTokenSource();
        var chrono = Stopwatch.StartNew();
        var fini = false;
        var commence = false;
        var avancement = new Progress<MiseAJourDesOutils.Avancement>(a =>
        {
            if (fini) return;
            if (!commence)
            {
                // Les premiers octets arrivent : c'est bien un fichier, l'afficheur se met en place.
                commence = true;
                Ecrire("Cette adresse mène à un fichier ordinaire, il est téléchargé tel quel.");
                BarreProgression.ClearValue(ForegroundProperty);
                BarreProgression.Visibility = Visibility.Visible;
                Mesures.Visibility = Visibility.Visible;
                MesureLiaison.Text = adresse.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS direct" : "HTTP direct";
                MesureFormat.Text = "fichier";
            }
            MontrerUnTransfert(a, chrono.Elapsed.TotalSeconds);
        });
        try
        {
            using var client = MiseAJourDesOutils.NouveauClient();
            var fichier = await FichierDirect.TelechargerAsync(client, adresse, dossier, avancement, arret.Token);
            fini = true;
            if (fichier is null) return false;

            BarreProgression.IsIndeterminate = false;
            BarreProgression.Visibility = Visibility.Visible;
            BarreProgression.Value = BarreProgression.Maximum;
            BarreProgression.SetResourceReference(ForegroundProperty, "Reussite");
            MesureReste.Text = "00:00";
            _dernierFichier = fichier;
            NomFichier.Text = Path.GetFileName(fichier);
            NomFichier.ToolTip = fichier;
            Derouler(Resultat, true);
            Terminer("TERMINÉ", "Ce n'était ni une vidéo ni une image : le fichier a été téléchargé tel quel.", Ton.Reussite);
            return true;
        }
        catch (OperationCanceledException) when (arret.IsCancellationRequested)
        {
            fini = true;
            BarreProgression.Visibility = Visibility.Collapsed;
            Mesures.Visibility = Visibility.Collapsed;
            Terminer("ANNULÉ", "", Ton.Neutre);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or OperationCanceledException)
        {
            fini = true;
            Ecrire($"Le téléchargement direct de cette adresse n'a pas abouti. {e.Message}");
            return false;
        }
        finally
        {
            fini = true;
            _arretDuTransfert = null;
        }
    }

    // ───────────────────────────── Vue des torrents ─────────────────────────────

    // Créée à la première visite : tant qu'on n'ouvre pas la vue, aria2 n'est pas lancé et rien n'est chargé.
    private VueTorrents? _vueTorrents;

    /// <summary>
    /// Les trois touches de la plaque, « Audio », « Vidéo » et « Torrents », changent la vue dans la même fenêtre.
    /// Audio et vidéo partagent les mêmes rangées, chacune avec ses réglages. Les torrents ont leur propre vue,
    /// et continuent d'avancer quand elle est cachée.
    /// </summary>
    private void ChangerDeVue(object sender, RoutedEventArgs e)
    {
        // Les touches préviennent dès la lecture du dessin, avant que le reste existe.
        if (VueTelechargement is null || ZoneTorrents is null || ChoixVueTorrents is null || BasculeCle is null
            || ChoixAudio is null || ChoixVideo is null) return;
        var torrents = ChoixVueTorrents.IsChecked == true;
        // Entre deux touches il y a un instant où aucune n'est enfoncée (Avalonia prévient aussi de celle qu'on quitte).
        if (!torrents && ChoixAudio.IsChecked != true && ChoixVideo.IsChecked != true) return;
        if (!torrents)
        {
            _audio = ChoixAudio.IsChecked == true;
            AdapterLesListes();
        }
        if (torrents && _vueTorrents is null)
        {
#if PORTABLE
            _vueTorrents = new VueTorrents(() => _dossier);
#else
            _vueTorrents = new VueTorrents(Resources, () => _dossier);
#endif
            // Le nombre de torrents en cours s'écrit sur la touche, pour qu'on le voie depuis l'autre vue.
            _vueTorrents.EnCoursChange += nombre => ChoixVueTorrents.Content = nombre > 0 ? $"Torrents ({nombre})" : "Torrents";
            ZoneTorrents.Child = _vueTorrents;
        }
        var torrentsAvant = ZoneTorrents.Visibility == Visibility.Visible;
        VueTelechargement.Visibility = torrents ? Visibility.Collapsed : Visibility.Visible;
        ZoneTorrents.Visibility = torrents ? Visibility.Visible : Visibility.Collapsed;
        // Le compte Spotify et le journal appartiennent à la vue de téléchargement : leur rangée du pied part avec elle.
        BasculeCle.Visibility = BasculeJournal.Visibility = torrents ? Visibility.Collapsed : Visibility.Visible;
        PiedDesBascules.Visibility = torrents ? Visibility.Collapsed : Visibility.Visible;
        if (torrents != torrentsAvant)
        {
            // On entre dans les torrents ou on en sort : toute la vue qui arrive monte de quelques points en apparaissant.
            FaireEntrer(torrents ? ZoneTorrents : VueTelechargement);
        }
        else if (!torrents)
        {
            // D'audio à vidéo, les rangées communes ne bougent pas. Seules les deux listes qui changent se renouvellent,
            // pendant que la rangée de l'égaliseur se déroule ou se replie.
            FaireEntrer(ChoixFormat);
            FaireEntrer(_audio ? ChoixQualiteSon : ChoixQualite);
        }
        GarderLaFenetreALEcran();
    }

    /// <summary>Passe sur la vue des torrents et la rend, pour y ajouter un torrent.</summary>
    private VueTorrents OuvrirLesTorrents()
    {
        ChoixVueTorrents.IsChecked = true;
        return _vueTorrents!;
    }

    // ───────────────────────────── Nouvelle version de l'application ─────────────────────────────

    private VersionDeLApplication.Publication? _nouvelleVersion;

    /// <summary>
    /// Regarde si une version plus récente de l'application est publiée, et montre alors le bouton qui l'installe.
    /// Sans réseau, ou si GitHub ne répond pas, il ne se passe rien : ce n'est qu'un avis.
    /// </summary>
    private async Task ChercherUneNouvelleVersionAsync()
    {
        VersionDeLApplication.EffacerLAncienne();
        try
        {
            using var client = MiseAJourDesOutils.NouveauClient();
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _nouvelleVersion = await VersionDeLApplication.ChercherAsync(client, delai.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }
        if (_nouvelleVersion is not { } publication) return;

        var numero = VersionDeLApplication.Lisible(publication.Numero);
        var dejaAnnoncee = BoutonNouvelleVersion.Visibility == Visibility.Visible;
        BoutonNouvelleVersion.Content = $"Installer la version {numero}";
        AutomationProperties.SetName(BoutonNouvelleVersion, $"Installer la version {numero} de Download Everything");
        BoutonNouvelleVersion.Visibility = Visibility.Visible;
        if (!dejaAnnoncee) Ecrire($"La version {numero} de Download Everything est disponible, le bouton en haut à droite l'installe.");
    }

#if PORTABLE
    /// <summary>
    /// Sous Linux l'application est installée par un paquet, dans un dossier du système qu'elle ne peut pas
    /// modifier elle-même. Le bouton ouvre donc la page où se trouve le nouveau paquet à installer.
    /// </summary>
    private void InstallerLaNouvelleVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            Plateforme.Ouvrir(VersionDeLApplication.PageDesVersions);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Etat($"Le navigateur n'a pas pu s'ouvrir. La nouvelle version se trouve sur {VersionDeLApplication.PageDesVersions}", Ton.Erreur, annoncer: true);
        }
    }
#else
    /// <summary>
    /// Télécharge la nouvelle version, contrôle son empreinte, la met à la place de celle-ci, puis relance
    /// l'application. Annulable tant que rien n'est remplacé. Quand tout réussit, la nouvelle version s'ouvre
    /// et celle-ci s'arrête aussitôt, depuis l'intérieur de l'installation : rien n'est exécuté ici après.
    /// </summary>
    private async void InstallerLaNouvelleVersion(object sender, RoutedEventArgs e)
    {
        if (_occupe || _nouvelleVersion is not { } publication) return;
        var numero = VersionDeLApplication.Lisible(publication.Numero);

        await Travailler(annulable: false, async () =>
        {
            ViderLaZoneDEtat();
            _miseAJour = true;
            _annulationDemandee = false;
            Ecrire("");
            Ecrire($"Installation de la version {numero} de Download Everything.");

            using var arret = _arretMiseAJour = new CancellationTokenSource();
            BoutonAnnuler.Visibility = Visibility.Visible;
            Mesures.Visibility = Visibility.Visible;
            BarreProgression.ClearValue(ForegroundProperty);
            BarreProgression.IsIndeterminate = true;
            BarreProgression.Visibility = Visibility.Visible;
            MesureLiaison.Text = "HTTPS direct";
            MesureFormat.Text = "application";
            Etat($"Téléchargement de la version {numero}…", annoncer: true);
            var chrono = Stopwatch.StartNew();
            // Après la fin, un dernier rapport encore en file ne doit plus toucher à l'affichage.
            var fini = false;
            var avancement = new Progress<MiseAJourDesOutils.Avancement>(a =>
            {
                if (!fini) MontrerUnTransfert(a, chrono.Elapsed.TotalSeconds);
            });
            try
            {
                using var client = MiseAJourDesOutils.NouveauClient();
                await VersionDeLApplication.InstallerEtRelancerAsync(client, publication, avancement, arret.Token);
            }
            catch (OperationCanceledException)
            {
                fini = true;
                BarreProgression.Visibility = Visibility.Collapsed;
                Mesures.Visibility = Visibility.Collapsed;
                Terminer("ANNULÉ", "La version actuelle reste en place, rien n'a été remplacé.", Ton.Neutre);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           or UnauthorizedAccessException or InvalidOperationException)
            {
                fini = true;
                BarreProgression.Visibility = Visibility.Collapsed;
                Mesures.Visibility = Visibility.Collapsed;
                BasculeJournal.IsChecked = true;
                Terminer("ERREUR", $"La version {numero} n'a pas pu être installée, la version actuelle reste en place. {ex.Message} "
                                   + $"Elle se télécharge aussi à la main sur {VersionDeLApplication.PageDesVersions}", Ton.Erreur);
            }
            finally
            {
                fini = true;
                _arretMiseAJour = null;
                BoutonAnnuler.Visibility = Visibility.Collapsed;
            }
        });
    }
#endif

#if PORTABLE
    // ───────────────────────────── Presse-papiers ─────────────────────────────

    // Le dernier contenu du presse-papiers déjà regardé. Vider le champ ne le fait pas revenir tout seul.
    private string _dernierPressePapiers = "";

    /// <summary>
    /// Quand la fenêtre revient au premier plan avec un champ vide, et que le presse-papiers ne contient que
    /// des adresses web, elles sont placées dans le champ, sélectionnées. Rien n'est lancé, et taper remplace tout.
    /// </summary>
    private async void RegarderLePressePapiers(object? sender, EventArgs e)
    {
        if (ChampAdresse is null || (ChampAdresse.Text ?? "").Trim().Length > 0 || Clipboard is not { } pressePapiers) return;
        try
        {
            var texte = await pressePapiers.TryGetTextAsync() ?? "";
            if (texte.Length is 0 or > 4000 || texte == _dernierPressePapiers || (ChampAdresse.Text ?? "").Trim().Length > 0) return;
            _dernierPressePapiers = texte;
            // Un paragraphe qui contient un lien parmi d'autres mots n'est pas une demande de téléchargement.
            var mots = texte.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var adresses = YtDlp.AdressesValides(texte);
            if (adresses.Count == 0 || adresses.Count != mots.Length) return;
            ChampAdresse.Text = string.Join(' ', adresses);
            ChampAdresse.SelectAll();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // Le presse-papiers n'a pas répondu, tant pis pour cette fois.
        }
    }
#else
    // ───────────────────────────── Glisser-déposer et presse-papiers ─────────────────────────────

    /// <summary>
    /// Ce qu'on peut tirer d'un dépôt : le texte ou le lien glissé depuis un navigateur, ou le contenu de
    /// raccourcis Internet (.url) et de petits fichiers texte déposés depuis l'Explorateur.
    /// </summary>
    private static string? TexteDepose(IDataObject donnees)
    {
        try
        {
            if (donnees.GetData(DataFormats.UnicodeText) is string texte && texte.Trim().Length > 0) return texte;
            if (donnees.GetData(DataFormats.Text) is string simple && simple.Trim().Length > 0) return simple;
            if (donnees.GetData(DataFormats.FileDrop) is not string[] fichiers) return null;

            var lus = new System.Text.StringBuilder();
            foreach (var fichier in fichiers.Take(50))
            {
                var extension = Path.GetExtension(fichier).ToLowerInvariant();
                if (extension == ".torrent")
                {
                    // Le chemin est écrit sous sa forme « file:///… », sans espace, pour tenir dans le champ parmi d'autres adresses.
                    lus.Append(new Uri(fichier).AbsoluteUri).Append(' ');
                    continue;
                }
                if (extension is not (".url" or ".txt") || new FileInfo(fichier).Length > 1 << 20) continue;
                foreach (var ligne in File.ReadLines(fichier))
                {
                    // Un raccourci Internet garde son adresse sur une ligne « URL=… ». Un fichier texte, une adresse par ligne.
                    if (extension == ".url" && !ligne.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                    lus.Append(extension == ".url" ? ligne[4..] : ligne).Append(' ');
                }
            }
            return lus.Length > 0 ? lus.ToString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void SurvolerAvecUnDepot(object sender, DragEventArgs e)
    {
        var utile = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text)
                    || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = utile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Un lien ou un fichier lâché n'importe où sur la fenêtre : ses adresses rejoignent le champ.</summary>
    private void RecevoirUnDepot(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var adresses = YtDlp.AdressesValides(TexteDepose(e.Data) ?? "");
        if (adresses.Count == 0)
        {
            if (!_occupe) Etat("Ce qui vient d'être déposé ne contient pas d'adresse web. Elle doit commencer par https://", Ton.Erreur, annoncer: true);
            return;
        }
        // Sur la vue des torrents, un fichier .torrent ou un lien magnet lâché démarre aussitôt.
        if (_vueTorrents is { IsVisible: true } vue)
        {
            var torrents = adresses.Where(Aria2.EstUnTorrent).ToList();
            foreach (var torrent in torrents) _ = vue.AjouterAsync(torrent, _dossier);
            if (torrents.Count > 0) return;
        }
        // Les adresses déjà dans le champ ne sont pas ajoutées deux fois.
        var presentes = YtDlp.AdressesValides(ChampAdresse.Text);
        var nouvelles = adresses.Where(a => !presentes.Contains(a)).ToList();
        if (nouvelles.Count > 0)
            ChampAdresse.Text = (ChampAdresse.Text.Trim() + " " + string.Join(' ', nouvelles)).Trim();
        ChampAdresse.CaretIndex = ChampAdresse.Text.Length;
        Activate();
        ChampAdresse.Focus();
    }

    // Le dernier contenu du presse-papiers déjà regardé. Vider le champ ne le fait pas revenir tout seul.
    private string _dernierPressePapiers = "";

    /// <summary>
    /// Quand la fenêtre revient au premier plan avec un champ vide, et que le presse-papiers ne contient que
    /// des adresses web, elles sont placées dans le champ, sélectionnées. Rien n'est lancé, et taper remplace tout.
    /// </summary>
    private void RegarderLePressePapiers(object? sender, EventArgs e)
    {
        if (ChampAdresse.Text.Trim().Length > 0) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var texte = Clipboard.GetText();
            if (texte.Length is 0 or > 4000 || texte == _dernierPressePapiers) return;
            _dernierPressePapiers = texte;
            // Un paragraphe qui contient un lien parmi d'autres mots n'est pas une demande de téléchargement.
            var mots = texte.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var adresses = YtDlp.AdressesValides(texte);
            if (adresses.Count == 0 || adresses.Count != mots.Length) return;
            ChampAdresse.Text = string.Join(' ', adresses);
            ChampAdresse.SelectAll();
        }
        catch (COMException)
        {
            // Le presse-papiers est tenu par un autre programme à cet instant, tant pis pour cette fois.
        }
    }
#endif

    /// <summary>
    /// Plusieurs liens collés ensemble. Chacun devient un travail de la file, et un lien Spotify se déplie
    /// en autant de travaux qu'il a de morceaux. Le tout est mené de front comme une playlist.
    /// </summary>
    private async Task TelechargerPlusieursAsync(string executable, List<string> adresses, string dossier)
    {
        var audioSeul = _audio;
        var format = (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "";
        var qualite = (ChoixQualite.SelectedItem as Choix)?.Valeur ?? "";
        Ecrire("");
        Ecrire($"{adresses.Count} adresses à traiter.");
        Etat("Préparation de la file…", annoncer: true);
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;

        var travaux = new List<Travail>();
        using var client = MiseAJourDesOutils.NouveauClient();
        // Le signal d'arrêt des documents que l'application rapatrie elle-même, pour le bouton Annuler.
        using var arret = new CancellationTokenSource();
        _arretDuTransfert = arret;
        using var _ = new Fin(() => _arretDuTransfert = null);
        foreach (var adresse in adresses)
        {
            if (_annulationDemandee) break;
            if (LiensDeMusique.Plateforme(adresse) is null)
            {
                // Le titre affiché est l'adresse, le temps que yt-dlp donne le vrai titre.
                var element = new ElementDePlaylist(_elements.Count + 1) { Titre = adresse };
                _elements.Add(element);
                if (FichierDirect.EstUnDocument(adresse))
                {
                    // Un document ou une archive : pris tel quel, sans passer par yt-dlp.
                    travaux.Add(new Travail(element, "erreur")
                    {
                        Direct = travail => FichierDirect.TelechargerAsync(client, adresse, dossier,
                            new Progress<MiseAJourDesOutils.Avancement>(a =>
                            {
                                travail.Recu = a.Recu;
                                travail.Total = a.Total;
                                if (a.Total <= 0) return;
                                travail.Element.Pourcent = Math.Clamp(100.0 * a.Recu / a.Total, 0, 100);
                                travail.Element.Etat = $"{Math.Floor(travail.Element.Pourcent):0} %";
                            }), arret.Token),
                    });
                    continue;
                }
                var demande = new Demande(adresse, audioSeul, format, qualite, dossier, PrefixeDeNom: "", QualiteDuSon: _qualiteDuSon, Egaliseur: _egaliseur);
                travaux.Add(new Travail(element, "erreur", suivi => YtDlp.ArgumentsTelechargement(demande, _complements, suivi))
                {
                    // Une adresse sans vidéo peut porter des images. En mode Audio, elles n'auraient pas de sens.
                    RecoursImages = audioSeul || !GalleryDl.PeutAider(adresse) ? null : () => GalleryDl.Arguments(adresse, dossier),
                });
                continue;
            }

            // Dans une file de liens, une playlist d'un autre s'arrête aux cent titres de la page publique.
            var (morceaux, _) = await LireUnLienSpotifyAsync(client, adresse);
            if (morceaux.Count == 0)
            {
                Ecrire($"ERROR: le lien {adresse} n'a pas pu être lu, il est laissé de côté.");
                continue;
            }
            // Un lien de plateforme de musique ne donne que du son, en MP3 si l'on était sur Vidéo.
            var formatDuSon = audioSeul ? format : "mp3";
            var enListe = morceaux.Count > 1;
            foreach (var morceau in morceaux)
            {
                var element = new ElementDePlaylist(_elements.Count + 1) { Titre = $"{morceau.Artiste} - {morceau.Titre}" };
                _elements.Add(element);
                travaux.Add(new Travail(element, "introuvable",
                    suivi => YtDlp.ArgumentsPourUnMorceau(morceau, formatDuSon, dossier, enListe, _complements, surYoutubeMusic: true, suivi, _qualiteDuSon, _egaliseur),
                    suivi => YtDlp.ArgumentsPourUnMorceau(morceau, formatDuSon, dossier, enListe, _complements, surYoutubeMusic: false, suivi, _qualiteDuSon, _egaliseur))
                {
                    CheminAttendu = YtDlp.CheminAttenduDuMorceau(morceau, formatDuSon, dossier, enListe),
                    TitreAttendu = morceau.Titre,
                });
            }
        }

        if (_annulationDemandee || travaux.Count == 0)
        {
            ConclureLaListe([], 0);
            return;
        }
        var (fichiers, echecs) = await MenerLaListeAsync(executable, travaux);
        ConclureLaListe(fichiers, echecs);
    }

    // ───────────────────────────── Compte Spotify ─────────────────────────────

    private CancellationTokenSource? _connexionEnCours;

    private void BasculerLaCle(object sender, RoutedEventArgs e)
    {
        var ouvert = BasculeCle.IsChecked == true;
        Derouler(ZoneCle, ouvert);
        if (!ouvert) return;
        ChampRetourSpotify.Text = CompteSpotify.AdresseDeRetour;
        if ((ChampIdSpotify.Text ?? "").Length == 0) ChampIdSpotify.Text = CompteSpotify.IdClient;
        MontrerLeCompte();
        GarderLaFenetreALEcran();
    }

    private void MontrerLeCompte(string? message = null, bool erreur = false)
    {
        var connecte = CompteSpotify.EstConnecte;
        BoutonDeconnecterSpotify.IsEnabled = connecte;
        BasculeCle.Content = connecte ? "Compte _Spotify, connecté" : "Compte _Spotify";
        EtatDeLaCle.Text = message ?? (connecte
            ? "Connecté. Vos playlists Spotify seront lues en entier, par ce compte."
            : "Non connecté. Les playlists sont lues sur la page publique de Spotify, limitée aux 100 premiers titres.");
        EtatDeLaCle.SetResourceReference(TextBlock.ForegroundProperty, erreur ? "Alerte" : "TexteDoux");
        if (message is not null) Annoncer(EtatDeLaCle);
    }

    private async void ConnecterSpotify(object sender, RoutedEventArgs e)
    {
        // Un second clic pendant l'attente annule la connexion en cours.
        if (_connexionEnCours is { } enCours)
        {
            enCours.Cancel();
            return;
        }

        var id = (ChampIdSpotify.Text ?? "").Trim();
        if (!CompteSpotify.IdValide(id))
        {
            MontrerLeCompte("Collez d'abord le Client ID de votre application Spotify. Il fait 32 caractères, des chiffres et des lettres de a à f.", erreur: true);
            ChampIdSpotify.Focus();
            return;
        }

        // Trois minutes pour se connecter dans le navigateur, largement de quoi taper un mot de passe.
        using var arret = _connexionEnCours = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        BoutonConnecterSpotify.Content = "Annuler la connexion";
        MontrerLeCompte("La page de connexion de Spotify s'ouvre dans votre navigateur. Connectez-vous, puis revenez ici.");
        try
        {
            using var client = MiseAJourDesOutils.NouveauClient();
            await CompteSpotify.ConnecterAsync(id, client, arret.Token);
            MontrerLeCompte();
            Ecrire("Compte Spotify connecté.");
        }
        catch (OperationCanceledException)
        {
            MontrerLeCompte("Connexion abandonnée, ou restée sans réponse pendant trois minutes.", erreur: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException
                                       or System.Net.Sockets.SocketException or System.Text.Json.JsonException or Win32Exception or KeyNotFoundException)
        {
            MontrerLeCompte($"La connexion n'a pas abouti. {ex.Message}", erreur: true);
        }
        finally
        {
            _connexionEnCours = null;
            BoutonConnecterSpotify.Content = "Se connecter";
        }
    }

    private void DeconnecterSpotify(object sender, RoutedEventArgs e)
    {
        CompteSpotify.Deconnecter();
        MontrerLeCompte("Déconnecté. Le jeton gardé sur ce PC est effacé.");
    }

#if PORTABLE
    private async void CopierLAdresseDeRetour(object sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } pressePapiers) return;
        await pressePapiers.SetTextAsync(CompteSpotify.AdresseDeRetour);
        // Cette adresse-là n'est pas un lien à télécharger, elle ne doit pas revenir dans le champ au retour du navigateur.
        _dernierPressePapiers = CompteSpotify.AdresseDeRetour;
        MontrerLeCompte("Adresse de retour copiée. Collez-la dans la case Redirect URIs de votre application Spotify.");
    }
#else
    private void CopierLAdresseDeRetour(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CompteSpotify.AdresseDeRetour);
            // Cette adresse-là n'est pas un lien à télécharger, elle ne doit pas revenir dans le champ au retour du navigateur.
            _dernierPressePapiers = CompteSpotify.AdresseDeRetour;
            MontrerLeCompte("Adresse de retour copiée. Collez-la dans la case Redirect URIs de votre application Spotify.");
        }
        catch (COMException)
        {
            MontrerLeCompte("Le presse-papiers est occupé, réessayez.", erreur: true);
        }
    }
#endif

    private void OuvrirLeTableauDeBordSpotify(object sender, RoutedEventArgs e)
    {
        try
        {
            // Adresse fixe, ouverte par le navigateur par défaut.
            Process.Start(new ProcessStartInfo(CompteSpotify.TableauDeBord) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MontrerLeCompte($"Le navigateur n'a pas pu s'ouvrir. {ex.Message}", erreur: true);
        }
    }

    /// <summary>
    /// Lit les morceaux d'un lien Spotify par la source la plus complète disponible. Pour une playlist et un
    /// compte connecté, le catalogue officiel, qui donne tout. Sinon, ou si Spotify refuse (playlist d'un autre),
    /// la page publique, limitée aux cent premiers titres. « incomplete » dit qu'il reste sans doute une suite à lire.
    /// </summary>
    private async Task<(List<Morceau> Morceaux, bool Incomplete)> LireUnLienSpotifyAsync(HttpClient client, string adresse)
    {
        // Deezer et Apple Music. Leurs catalogues publics rendent la liste entière, il n'y a pas de suite à lire.
        if (LiensDeMusique.Plateforme(adresse) is "Deezer" or "Apple Music")
        {
            var plateforme = LiensDeMusique.Plateforme(adresse)!;
            try
            {
                using var delai = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var avancement = new Progress<(int Lus, int Total)>(p => Etat($"Lecture sur {plateforme}, {p.Lus} sur {p.Total}…"));
                var lus = plateforme == "Deezer"
                    ? await CatalogueDeezer.LireAsync(client, adresse, avancement, delai.Token)
                    : await CatalogueApple.LireAsync(client, adresse, avancement, delai.Token);
                if (lus.Count > 0) Ecrire($"Lu sur le catalogue public de {plateforme}, {lus.Count} {(lus.Count > 1 ? "morceaux" : "morceau")}.");
                return (lus, false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException
                                           or System.Text.Json.JsonException or KeyNotFoundException or UriFormatException)
            {
                Ecrire($"ERROR: {plateforme} n'a pas pu être lu. {ex.Message}");
                return ([], false);
            }
        }

        if (CompteSpotify.EstConnecte && PageSpotify.Identifier(adresse) is ("playlist", var idPlaylist))
        {
            try
            {
                using var delai = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var avancement = new Progress<(int Lus, int Total)>(p => Etat($"Lecture de la playlist par votre compte Spotify, {p.Lus} sur {p.Total}…"));
                var (nom, lus) = await CompteSpotify.LirePlaylistAsync(client, idPlaylist, avancement, delai.Token);
                if (lus.Count > 0)
                {
                    Ecrire($"Playlist « {nom} » lue par votre compte Spotify, {lus.Count} morceaux.");
                    return (lus, false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException
                                           or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // Le cas courant : la playlist d'un autre, que Spotify ne laisse pas lire ainsi. On passe à la page publique.
                Ecrire($"Lecture par votre compte Spotify impossible. {ex.Message} Passage à la page publique.");
            }
        }

        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (morceaux, incomplete) = await PageSpotify.LireAsync(client, adresse, delai.Token);
            if (morceaux.Count > 0) Ecrire($"Lu sur la page publique de Spotify, {morceaux.Count} {(morceaux.Count > 1 ? "morceaux" : "morceau")}.");
            return (morceaux, incomplete);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Ecrire($"La page publique de Spotify n'a pas pu être lue. {ex.Message}");
            return ([], false);
        }
    }

    // ───────────────────────────── Liens Spotify ─────────────────────────────

    /// <summary>
    /// Télécharge un lien Spotify en deux temps. La liste des morceaux est lue sur la page publique de Spotify
    /// (ou par spotDL en recours), puis yt-dlp retrouve et télécharge chacun, l'un après l'autre.
    /// À chaque étape la source la plus sûre est essayée d'abord, la suivante prend le relais sans rien demander.
    /// </summary>
    private async Task TelechargerDepuisSpotifyAsync(string executable, string adresse, string dossier)
    {
        if (_complements.Ffmpeg is null)
        {
            Terminer("ERREUR", "Les liens Spotify ont besoin de ffmpeg, qui manque dans le dossier des outils. "
                               + "Le bouton Tout mettre à jour le réinstalle.", Ton.Erreur);
            return;
        }

        // Un lien de plateforme de musique ne donne que du son, quel que soit le choix Vidéo ou Audio.
        var source = LiensDeMusique.Plateforme(adresse) ?? "Spotify";
        var format = _audio ? (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "mp3" : "mp3";
        Ecrire("");
        Ecrire($"Lecture du lien {source} {adresse}");
        if (!_audio) Ecrire($"Un lien {source} ne donne que du son, les fichiers seront en MP3.");

        // Premier temps, la description des morceaux. La source la plus rapide d'abord, la page publique de Spotify.
        Etat($"Lecture du lien {source}…", annoncer: true);
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;
        List<Morceau> morceaux;
        bool incomplete;
        using (var client = MiseAJourDesOutils.NouveauClient())
            (morceaux, incomplete) = await LireUnLienSpotifyAsync(client, adresse);

        // Recours quand ni le compte ni la page publique n'ont rien donné, spotDL. Bien plus lent, mais il passe par le service
        // officiel de Spotify et lit tout. Ici il faut l'attendre, il n'y a rien d'autre à télécharger d'ici là.
        if (morceaux.Count == 0 && !_annulationDemandee && source == "Spotify" && SpotDl.Trouver() is { } spotdl)
        {
            Ecrire("Lecture par spotDL, cela peut prendre plusieurs minutes.");
            SpotDl.EffacerLesMorceaux();
            await LancerAsync(SpotDl.Preparer(spotdl, SpotDl.ArgumentsDeLecture(adresse, _complements.Ffmpeg)),
                "Lecture du lien par spotDL, cela peut prendre plusieurs minutes…", RecevoirLigneDeSpotDl);
            var parSpotDl = _annulationDemandee ? [] : SpotDl.LireLesMorceaux();
            SpotDl.EffacerLesMorceaux();
            if (parSpotDl.Count > 0 && parSpotDl.Count >= morceaux.Count) morceaux = parSpotDl;
        }

        if (_annulationDemandee)
        {
            BarreProgression.Visibility = Visibility.Collapsed;
            Terminer("ANNULÉ", "", Ton.Neutre);
            return;
        }
        if (morceaux.Count == 0)
        {
            BarreProgression.Visibility = Visibility.Collapsed;
            BasculeJournal.IsChecked = true;
            Terminer("ERREUR", $"Ce lien {source} n'a pas pu être lu, le détail est dans le journal. "
                               + "Vérifiez que le lien est public, ou essayez le bouton Tout mettre à jour.", Ton.Erreur);
            return;
        }

        // Second temps, un téléchargement par morceau, plusieurs à la fois. Pour chacun, YouTube Music d'abord,
        // où se trouvent les enregistrements officiels, puis YouTube en second recours.
        Ecrire($"{morceaux.Count} {(morceaux.Count > 1 ? "morceaux à télécharger" : "morceau à télécharger")}.");
        var enListe = morceaux.Count > 1;

        Travail Creer(Morceau morceau)
        {
            var element = new ElementDePlaylist(_elements.Count + 1) { Titre = $"{morceau.Artiste} - {morceau.Titre}" };
            _elements.Add(element);
            return new Travail(element, "introuvable",
                suivi => YtDlp.ArgumentsPourUnMorceau(morceau, format, dossier, enListe, _complements, surYoutubeMusic: true, suivi, _qualiteDuSon, _egaliseur),
                suivi => YtDlp.ArgumentsPourUnMorceau(morceau, format, dossier, enListe, _complements, surYoutubeMusic: false, suivi, _qualiteDuSon, _egaliseur))
            {
                // Le nom du fichier est connu d'avance. S'il est déjà dans le dossier, le morceau est sauté sans rien lancer.
                ChercherExistant = () => YtDlp.MorceauDejaPresent(morceau, format, dossier, enListe),
                CheminAttendu = YtDlp.CheminAttenduDuMorceau(morceau, format, dossier, enListe),
                TitreAttendu = morceau.Titre,
            };
        }

        var travaux = morceaux.Select(Creer).ToList();

        // Grande playlist : la page publique ne donne que les cent premiers titres. Ils partent tout de suite,
        // et spotDL lit la suite pendant qu'ils se téléchargent, au lieu de faire attendre devant une fenêtre immobile.
        Task<List<Travail>>? suite = null;
        if (incomplete && source == "Spotify" && SpotDl.Trouver() is { } outil)
            suite = LireLaSuiteAsync(outil, adresse, morceaux, Creer);

        var (fichiers, echecs) = await MenerLaListeAsync(executable, travaux, suite);
        ConclureLaListe(fichiers, echecs);
    }

    // Le spotDL qui lit la suite d'une grande playlist pendant que les premiers morceaux se téléchargent.
    private Process? _lectureEnFond;

    /// <summary>
    /// Fait lire toute la playlist par spotDL et rend les travaux des morceaux qui ne sont pas déjà partis.
    /// </summary>
    private async Task<List<Travail>> LireLaSuiteAsync(string spotdl, string adresse, List<Morceau> dejaPartis, Func<Morceau, Travail> creer)
    {
        Ecrire($"Grande playlist. Les {dejaPartis.Count} premiers morceaux partent tout de suite, spotDL lit la suite pendant ce temps.");
        SpotDl.EffacerLesMorceaux();
        try
        {
            using var processus = new Process { StartInfo = SpotDl.Preparer(spotdl, SpotDl.ArgumentsDeLecture(adresse, _complements.Ffmpeg!)) };
            processus.OutputDataReceived += (_, e) => Transmettre(e.Data, RecevoirLigneDeSpotDl);
            processus.ErrorDataReceived += (_, e) => Transmettre(e.Data, RecevoirLigneDeSpotDl);
            processus.Start();
            _lectureEnFond = processus;
            processus.BeginOutputReadLine();
            processus.BeginErrorReadLine();
            await processus.WaitForExitAsync();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Ecrire($"ERROR: impossible de lancer spotDL pour lire la suite de la playlist. {e.Message}");
        }
        finally
        {
            _lectureEnFond = null;
        }
        if (_annulationDemandee) return [];

        var tous = SpotDl.LireLesMorceaux();
        SpotDl.EffacerLesMorceaux();
        // spotDL ne rend pas toujours les morceaux dans l'ordre de la playlist. On reconnaît donc ceux qui sont
        // déjà partis à leur artiste et à leur titre, pas à leur position. La durée n'entre pas dans la comparaison,
        // les deux sources l'arrondissent différemment et un même morceau passerait pour deux.
        static string Cle(Morceau m) => $"{m.Artiste}|{m.Titre}".ToLowerInvariant();
        var connus = dejaPartis.Select(Cle).ToHashSet();
        // Un ordre qui ne dépend pas de celui de spotDL, changeant d'une lecture à l'autre : la position dans la
        // playlist quand il la donne, sinon le numéro de piste, sinon l'ordre alphabétique. Chaque morceau garde
        // ainsi le même numéro si l'on relance, et son fichier est reconnu.
        var reste = tous.Where(m => connus.Add(Cle(m)))
            .OrderBy(m => m.Rang > 0 ? m.Rang : int.MaxValue)
            .ThenBy(m => m.Piste > 0 ? m.Piste : int.MaxValue)
            .ThenBy(Cle, StringComparer.Ordinal)
            .ToList();
        var rang = dejaPartis.Count;
        var travaux = new List<Travail>();
        foreach (var morceau in reste)
        {
            rang++;
            // Même largeur de rang que les premiers, pour que les fichiers se rangent dans l'ordre.
            travaux.Add(creer(morceau with { Rang = morceau.Rang > 0 ? morceau.Rang : rang, LargeurDuRang = 3 }));
        }
        Ecrire(travaux.Count > 0
            ? $"Suite de la playlist lue, {travaux.Count} morceaux de plus."
            : "Suite de la playlist lue, il n'y avait rien de plus.");
        return travaux;
    }

    /// <summary>Lignes de spotDL pendant la lecture du lien. Elles vont au journal, les erreurs en rouge.</summary>
    // « Found 415 songs in … », que spotDL écrit dès qu'il connaît la taille de la playlist,
    // bien avant d'avoir fini de lire le détail de chaque titre.
    [GeneratedRegex(@"Found (\d+) songs")]
    private static partial Regex TailleAnnoncee();

    // Le nombre de titres de la playlist tel que spotDL l'annonce, quand il dépasse ce qui est déjà dans la file.
    private int _totalAnnonce;

    private void RecevoirLigneDeSpotDl(string brute)
    {
        var ligne = brute.Trim();
        if (ligne.Length == 0) return;
        if (_listeMaitrisee && TailleAnnoncee().Match(ligne) is { Success: true } taille
            && int.TryParse(taille.Groups[1].Value, out var annonce) && annonce > _total)
        {
            // L'afficheur compte désormais sur le vrai total, pour qu'on ne croie pas la playlist arrêtée au premier lot.
            _totalAnnonce = annonce;
            Etat($"{annonce} titres dans la playlist. Les {_total} premiers sont partis, les {annonce - _total} suivants "
                 + "arriveront dès que spotDL aura fini de lire leurs détails, ce qui prend plusieurs minutes.", annoncer: true);
        }
        Ecrire(ligne.Contains("Error", StringComparison.Ordinal) && !ligne.StartsWith("ERROR", StringComparison.Ordinal) ? "ERROR: " + ligne : ligne,
            deLOutil: true);
    }

    private void RecevoirLigne(string ligne)
    {
        if (ligne.StartsWith(YtDlp.MarqueDeMesure, StringComparison.Ordinal))
        {
            // Les lignes de mesure nourrissent l'afficheur, elles n'entrent pas dans le journal.
            LireUneMesure(ligne.Split('|', YtDlp.NombreDeChampsDeMesure));
            return;
        }

        var annonce = AnnonceDElement().Match(ligne);
        if (annonce.Success)
        {
            if (!_listeMaitrisee)
                CommencerUnElement(int.Parse(annonce.Groups[1].Value, CultureInfo.InvariantCulture),
                                   int.Parse(annonce.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        else if (ligne.StartsWith("ERROR", StringComparison.Ordinal))
        {
            // Pour un lien Spotify, une erreur sur un résultat de recherche n'est pas l'échec du morceau, un autre résultat peut convenir.
            if (!_listeMaitrisee) MarquerLElementCourant("erreur");
            if (EstUnRefusDeYoutube(ligne)) _refusDeYoutube++;
        }
        else if (ligne.Contains(YtDlp.MarqueDImageEcartee, StringComparison.Ordinal))
        {
            // Ce que yt-dlp a trouvé est une image. Il la laisse, gallery-dl la prendra.
            _imageEcartee = true;
        }
        else if (ligne.EndsWith("has already been downloaded", StringComparison.Ordinal))
        {
            // yt-dlp ne retélécharge pas un fichier qui porte déjà ce nom dans le dossier. Il faut le dire,
            // sinon « Terminé » laisserait croire à un nouveau transfert.
            _dejaPresent = true;
        }
        else if (ligne.StartsWith("[download] Destination:", StringComparison.Ordinal))
        {
            // Un nouveau fichier commence, par exemple la piste son après l'image.
            BarreProgression.IsIndeterminate = false;
            if (_total <= 1) BarreProgression.Value = 0;
        }
        else if (EtapesDeFfmpeg.Any(etape => ligne.StartsWith(etape, StringComparison.Ordinal)))
        {
            if (_total <= 1)
            {
                BarreProgression.IsIndeterminate = true;
                BarreDesTaches.ProgressState = TaskbarItemProgressState.Indeterminate;
                Afficher("", Ton.Neutre);
            }
            if (ElementCourant() is { Etat: not "erreur" } element) element.Etat = "assemblage";
            Etat("Assemblage ou conversion en cours…");
        }
        Ecrire(ligne, deLOutil: true);
    }

    /// <summary>Une ligne de mesure de yt-dlp, déjà découpée. L'ordre des champs est celui du gabarit de YtDlp.</summary>
    private void LireUneMesure(string[] champs)
    {
        if (champs.Length < YtDlp.NombreDeChampsDeMesure) return;
        var recu = Nombre(champs[2]);
        var total = Nombre(champs[3]) ?? Nombre(champs[4]);
        var debit = Nombre(champs[5]);
        var reste = Nombre(champs[6]);
        var ecoule = Nombre(champs[7]);
        var part = recu is not null && total > 0 ? Math.Clamp(recu.Value / total.Value, 0, 1) : (double?)null;
        var fini = champs[1] == "finished";

        MesureDebit.Text = debit > 0 ? $"{Octets(debit.Value)}/s" : fini && recu > 0 && ecoule > 0 ? $"{Octets(recu.Value / ecoule.Value)}/s" : "—";
        MesureRecu.Text = recu is null ? "—" : total > 0 && !fini ? $"{Octets(recu.Value)} / {Octets(total.Value)}" : Octets(recu.Value);
        MesureReste.Text = fini ? "00:00" : reste is null ? "—" : Duree(reste.Value);
        MesureEcoule.Text = ecoule is null ? "—" : Duree(ecoule.Value);
        MesureLiaison.Text = Liaison(champs[10], champs[8], champs[9]);
        MesureFormat.Text = champs[11] switch
        {
            "NA" => "—",
            "audio only" => "son seul",
            var definition => definition.Replace("x", " × "),
        };
        if (TexteEtat.Text is "Recherche de la vidéo…" or "Assemblage ou conversion en cours…") Etat("");

        var element = ElementCourant();
        if (element is not null)
        {
            // Pour un lien Spotify le titre affiché reste celui de Spotify, pas celui de la vidéo trouvée.
            if (champs[12] != "NA" && !_listeMaitrisee) element.Titre = champs[12];
            if (part is not null) element.Pourcent = part.Value * 100;
            if (element.Etat != "erreur") element.Etat = part is null ? "en cours" : $"{Math.Floor(part.Value * 100):0} %";
        }

        if (_total > 1)
        {
            // Playlist. L'afficheur compte les vidéos, la barre du haut donne l'avancement de l'ensemble.
            var ensemble = (_courant - 1 + (part ?? 0)) / _total;
            Avancer(ensemble * 100);
            Afficher($"{_courant} / {_total}", Ton.Neutre);
        }
        else if (part is not null)
        {
            Avancer(part.Value * 100);
            Afficher($"{Math.Floor(part.Value * 100):0} %", Ton.Neutre);
        }

        if (fini && recu is not null)
            Ecrire($"[download] 100 % de {Octets(recu.Value)}" + (ecoule is null ? "" : $" en {Duree(ecoule.Value)}"));
    }

    private void Avancer(double pourcent)
    {
        BarreProgression.IsIndeterminate = false;
        BarreProgression.Value = pourcent;
        BarreDesTaches.ProgressState = TaskbarItemProgressState.Normal;
        BarreDesTaches.ProgressValue = pourcent / 100;
    }

    // ───────────────────────────── Playlist ─────────────────────────────

    private ElementDePlaylist? ElementCourant() =>
        _courant >= 1 && _courant <= _elements.Count ? _elements[_courant - 1] : null;

    private void CommencerUnElement(int numero, int total)
    {
        // La vidéo précédente est finie, sauf si elle a échoué.
        if (ElementCourant() is { Etat: not "erreur" } precedent)
        {
            precedent.Etat = "terminé";
            precedent.Pourcent = 100;
        }

        _courant = numero;
        _total = total;
        // Au-delà de quelques centaines de lignes la liste n'aide plus personne, la barre d'ensemble suffit.
        while (_elements.Count < Math.Min(total, 500)) _elements.Add(new ElementDePlaylist(_elements.Count + 1));
        if (ElementCourant() is { } element) element.Etat = "en cours";

        if (total > 1)
        {
            BasculeElements.Visibility = Visibility.Visible;
            Afficher($"{numero} / {total}", Ton.Neutre);
            Avancer(100.0 * (numero - 1) / total);
        }
    }

    private void MarquerLElementCourant(string etat)
    {
        if (ElementCourant() is not { } element || element.Etat == "erreur") return;
        element.Etat = etat;
        if (etat == "terminé") element.Pourcent = 100;
    }

    // ───────────────────────────── Mise en forme des mesures ─────────────────────────────

    private static double? Nombre(string texte) =>
        double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out var valeur) ? valeur : null;

    /// <summary>Un nombre d'octets en ko, Mo ou Go, à la française. yt-dlp compte en multiples de 1024.</summary>
    internal static string Octets(double octets) => octets switch
    {
        < 1024 * 1024 => string.Create(Francais, $"{octets / 1024:0} ko"),
        < 1024.0 * 1024 * 1024 => string.Create(Francais, $"{octets / (1024 * 1024):0.0} Mo"),
        _ => string.Create(Francais, $"{octets / (1024.0 * 1024 * 1024):0.00} Go"),
    };

    internal static string Duree(double secondes)
    {
        var duree = TimeSpan.FromSeconds(Math.Max(0, Math.Round(secondes)));
        return duree.TotalHours >= 1 ? duree.ToString(@"h\:mm\:ss") : duree.ToString(@"mm\:ss");
    }

    /// <summary>Le type de liaison avec le serveur, et le fragment en cours quand la vidéo arrive en morceaux.</summary>
    private static string Liaison(string protocole, string fragment, string fragments)
    {
        // Pour une vidéo en deux pistes, yt-dlp écrit les deux protocoles réunis par un « + ».
        var premier = protocole.Split('+')[0];
        var nom = premier switch
        {
            "NA" or "" => "—",
            _ when premier.Contains("m3u8", StringComparison.Ordinal) => "HLS",
            _ when premier.Contains("dash", StringComparison.Ordinal) => "DASH",
            "https" => "HTTPS direct",
            "http" => "HTTP direct",
            _ => premier.ToUpperInvariant(),
        };
        return Nombre(fragments) is > 1 and var n && Nombre(fragment) is { } i ? $"{nom} {i:0}/{n:0}" : nom;
    }

    // ───────────────────────────── Affichage ─────────────────────────────

    /// <summary>Une ligne d'un travail de liste, précédée de son rang pour s'y retrouver quand cinq avancent ensemble.</summary>
    private void Ecrire(string ligne, int rang) =>
        Ecrire($"[{rang:00}] {ligne}", enRouge: ligne.StartsWith("ERROR", StringComparison.Ordinal), deLOutil: true);

    // Un journal sans fin finirait par peser, on ne garde donc que les dernières lignes.
    private const int LignesGardees = 4000;

    /// <param name="deLOutil">
    /// Vrai pour une ligne recopiée de yt-dlp ou de spotDL. Quand le journal est plein, ce sont ces lignes-là qui
    /// partent. Celles que l'application écrit elle-même (ce qui a été lu, pourquoi une voie a été écartée, le
    /// résultat) restent toujours : une longue playlist à dix de front en chasserait sinon les explications utiles.
    /// </param>
#if PORTABLE
    private void Ecrire(string ligne, bool? enRouge = null, bool deLOutil = false)
    {
        // Une ligne vide garde sa hauteur dans la liste grâce à une espace.
        _journal.Add(new LigneDeJournal(ligne.Length > 0 ? ligne : " ", enRouge ?? ligne.StartsWith("ERROR", StringComparison.Ordinal), deLOutil));
        while (_journal.Count > LignesGardees)
            _journal.Remove(_journal.FirstOrDefault(l => l.DeLOutil) ?? _journal[0]);
        // Le défilement attend que la nouvelle ligne soit posée à l'écran.
        if (Journal.IsVisible) Dispatcher.UIThread.Post(Journal.ScrollToEnd, DispatcherPriority.Background);
    }
#else
    private void Ecrire(string ligne, bool? enRouge = null, bool deLOutil = false)
    {
        var paragraphe = new Paragraph(new Run(ligne)) { Tag = deLOutil ? "outil" : null };
        if (enRouge ?? ligne.StartsWith("ERROR", StringComparison.Ordinal))
            paragraphe.Foreground = (Brush)FindResource("Alerte");
        var blocs = Journal.Document.Blocks;
        blocs.Add(paragraphe);
        while (blocs.Count > LignesGardees)
            blocs.Remove(blocs.FirstOrDefault(b => b.Tag is "outil") ?? blocs.FirstBlock);
        if (Journal.IsVisible) Journal.ScrollToEnd();
    }
#endif

    /// <summary>Remet la zone d'état à neuf avant un nouveau travail.</summary>
    private void ViderLaZoneDEtat()
    {
        Resultat.Visibility = Visibility.Collapsed;
        Mesures.Visibility = Visibility.Collapsed;
        BarreProgression.Visibility = Visibility.Collapsed;
        BasculeElements.IsChecked = false;
        BasculeElements.Visibility = Visibility.Collapsed;
        _elements.Clear();
        _courant = _total = 0;
        _totalAnnonce = 0;
        _refusDeYoutube = 0;
        _youtubeBloque = false;
        _dejaPresent = false;
        _imageEcartee = false;
        _listeMaitrisee = false;
        foreach (var mesure in new[] { MesureDebit, MesureRecu, MesureReste, MesureEcoule, MesureLiaison, MesureFormat })
            mesure.Text = "—";
        Afficher("", Ton.Neutre);
        Etat("");
    }

    /// <summary>
    /// L'afficheur, dans la colonne des étiquettes. Un pourcentage s'y écrit en grand, un compte de playlist
    /// un peu moins, un mot de fin (TERMINÉ, ERREUR) en capitales serrées. Vide, il laisse la place à l'étiquette ÉTAT.
    /// </summary>
    private void Afficher(string texte, Ton ton)
    {
        Afficheur.Text = texte;
        AutomationProperties.SetName(Afficheur, texte);
        Afficheur.Visibility = texte.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EtiquetteEtat.Visibility = texte.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var mot = texte.Length > 0 && !char.IsDigit(texte[0]);
        Afficheur.FontSize = mot ? 16 : texte.Contains('/') ? 21 : 28;
        Afficheur.FontWeight = mot ? FontWeights.SemiBold : FontWeights.Normal;
        Afficheur.FontStretch = mot ? FontStretches.SemiCondensed : FontStretches.Normal;
        Afficheur.SetResourceReference(TextBlock.ForegroundProperty, ton switch
        {
            Ton.Erreur => "Alerte",
            Ton.Reussite => "Reussite",
            _ => mot ? "TexteDoux" : "Texte",
        });
        MontrerOuCacherLaZone();
    }

    /// <summary>
    /// Écrit le message d'état. « annoncer » le fait lire par les lecteurs d'écran. On le réserve aux moments
    /// qui comptent (début, fin, erreur), pas aux mesures qui changent plusieurs fois par seconde.
    /// </summary>
    private void Etat(string message, Ton ton = Ton.Neutre, bool annoncer = false)
    {
        TexteEtat.Text = message;
        TexteEtat.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Les couleurs viennent de la palette de la fenêtre, une seule source pour tout l'habillage.
        TexteEtat.SetResourceReference(TextBlock.ForegroundProperty, ton == Ton.Erreur ? "Alerte" : "TexteDoux");
        MontrerOuCacherLaZone();
        if (!annoncer || message.Length == 0) return;
        AutomationProperties.SetLiveSetting(TexteEtat, ton == Ton.Erreur ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Annoncer(TexteEtat);
    }

    private void MontrerOuCacherLaZone() =>
        // « ?? "" » : sous Avalonia un texte jamais rempli vaut null, là où WPF rend une chaîne vide.
        Derouler(ZoneEtat, (TexteEtat.Text ?? "").Length > 0 || (Afficheur.Text ?? "").Length > 0
                           || BarreProgression.Visibility == Visibility.Visible || Mesures.Visibility == Visibility.Visible);

#if PORTABLE
    // Les annonces aux lecteurs d'écran passent par un mécanisme propre à Windows. Sous Linux, rien de plus à faire ici.
    private static void Annoncer(Control element)
    {
    }
#else
    private static void Annoncer(UIElement element) =>
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
#endif

    /// <summary>Fin d'un travail. Le mot va dans l'afficheur, la phrase éventuelle dessous, et le tout au journal.</summary>
    private void Terminer(string mot, string phrase, Ton ton, bool motEnVert = false)
    {
        Afficher(mot, motEnVert ? Ton.Reussite : ton);
        Etat(phrase, ton);
        var resume = phrase.Length > 0 ? phrase : mot.Length > 0 ? char.ToUpper(mot[0], Francais) + mot[1..].ToLower(Francais) : "";
        if (resume.Length > 0) Ecrire(resume);

        // Une seule annonce pour les lecteurs d'écran, le mot puis la phrase.
        if (mot.Length > 0 && phrase.Length == 0)
        {
            TexteEtat.Text = "";
            AutomationProperties.SetName(Afficheur, resume);
            AutomationProperties.SetLiveSetting(Afficheur, AutomationLiveSetting.Polite);
            Annoncer(Afficheur);
        }
        else if (phrase.Length > 0)
        {
            AutomationProperties.SetLiveSetting(TexteEtat, ton == Ton.Erreur ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            Annoncer(TexteEtat);
        }
    }
}
