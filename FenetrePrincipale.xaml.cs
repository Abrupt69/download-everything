using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

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
    public string Etat { get => _etat; set => Changer(ref _etat, value, nameof(Etat)); }

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
        ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".flac", ".wav", ".mka", ".weba",
    };

    public FenetrePrincipale()
    {
        InitializeComponent();

        // La hauteur suit le contenu. Sur un petit écran la fenêtre s'arrête à la zone de travail et son contenu défile.
        MaxHeight = SystemParameters.WorkArea.Height;

        Elements.ItemsSource = _elements;
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
        MontrerLeDossier(Reglages.LireDossier());
        ChoixQualite.ItemsSource = YtDlp.Qualites;
        ChoixQualite.SelectedIndex = 0;
        AdapterLesListes();

        Loaded += async (_, _) =>
        {
            ChampAdresse.Focus();
            await Travailler(annulable: false, () => VerifierYtDlpAsync());
        };
        Closing += FermerLaFenetre;
    }

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
    }

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr fenetre, int attribut, ref int valeur, int taille);

    // ───────────────────────────── Listes de choix ─────────────────────────────

    private void ChangerDeContenu(object sender, RoutedEventArgs e) => AdapterLesListes();

    /// <summary>
    /// Remplit la liste des formats selon le contenu choisi. Sans ffmpeg aucune conversion n'est possible,
    /// la liste ne propose alors que le format d'origine du site.
    /// </summary>
    private void AdapterLesListes()
    {
        // Les touches préviennent dès la lecture du XAML, avant que les listes existent.
        if (ChoixFormat is null || ChoixQualite is null) return;

        var audio = ChoixAudio.IsChecked == true;
        var avecFfmpeg = _complements.Ffmpeg is not null;
        Choix[] formats = !avecFfmpeg ? [YtDlp.FormatDOrigine] : audio ? YtDlp.FormatsAudio : YtDlp.FormatsVideo;

        if (!ReferenceEquals(ChoixFormat.ItemsSource, formats))
        {
            ChoixFormat.ItemsSource = formats;
            ChoixFormat.SelectedIndex = 0;
        }
        ChoixFormat.IsEnabled = avecFfmpeg;
        ChoixQualite.IsEnabled = !audio;
    }

    // ───────────────────────────── Petites actions ─────────────────────────────

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

    private void BasculerLeJournal(object sender, RoutedEventArgs e)
    {
        var ouvert = BasculeJournal.IsChecked == true;
        Journal.Visibility = ouvert ? Visibility.Visible : Visibility.Collapsed;
        BasculeJournal.Content = ouvert ? "Masquer le _journal" : "Afficher le _journal";
        if (!ouvert) return;
        Journal.ScrollToEnd();
        GarderLaFenetreALEcran();
    }

    private void BasculerLesElements(object sender, RoutedEventArgs e)
    {
        var ouvert = BasculeElements.IsChecked == true;
        CadreElements.Visibility = ouvert ? Visibility.Visible : Visibility.Collapsed;
        BasculeElements.Content = ouvert ? "Masquer le _détail" : "Afficher le _détail";
        if (ouvert) GarderLaFenetreALEcran();
    }

    /// <summary>La fenêtre vient de grandir vers le bas. Si elle dépasse de l'écran, on la remonte.</summary>
    private void GarderLaFenetreALEcran() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var bas = SystemParameters.WorkArea.Bottom;
        if (Top + ActualHeight > bas) Top = Math.Max(SystemParameters.WorkArea.Top, bas - ActualHeight);
    });

    // ───────────────────────────── Outils présents ou non ─────────────────────────────

    /// <summary>
    /// S'assure que yt-dlp est là et répond. À la première ouverture sur un PC, dépose d'abord les programmes
    /// que l'exécutable transporte. Rend le chemin de yt-dlp, ou null après avoir affiché un bandeau
    /// qui dit où poser le fichier et où le trouver.
    /// </summary>
    private async Task<string?> VerifierYtDlpAsync()
    {
        var vientDEtreDeploye = false;
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
            VersionFfmpeg.Text = VersionDeno.Text = VersionSpotDl.Text = "";
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
        VersionSpotDl.Text = SpotDl.Trouver() is null ? "spotDL absent, pas de liens Spotify"
            : versions.TryGetValue("spotdl", out var spotdl) ? $"spotDL {spotdl.TrimStart('v')}" : "spotDL prêt";
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

            // Plusieurs liens collés ensemble forment une file, menée de front.
            if (adresses.Count > 1)
            {
                await TelechargerPlusieursAsync(executable, adresses, dossier);
                return;
            }

            // Un lien Spotify suit son propre parcours, tout le reste va droit à yt-dlp.
            if (SpotDl.EstUneAdresseSpotify(adresse))
            {
                await TelechargerDepuisSpotifyAsync(executable, adresse, dossier);
                return;
            }

            Ecrire("");
            Ecrire($"Téléchargement de {adresse}");

            var audioSeul = ChoixAudio.IsChecked == true;
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
                        var uneVideo = new Demande(video, audioSeul, format, qualite, dossier, YtDlp.PrefixeDeListe(nomDeListe, i + 1));
                        travaux.Add(new Travail(element, "erreur", suivi => YtDlp.ArgumentsTelechargement(uneVideo, _complements, suivi)));
                    }
                    var (recus, echecs) = await MenerLaListeAsync(executable, travaux);
                    ConclureLaListe(recus, echecs);
                    return;
                }
                // Ce n'était pas une playlist. La vidéo seule suit le chemin habituel, sans sous-dossier.
            }

            var demande = new Demande(adresse, audioSeul, format, qualite, dossier, PrefixeDeNom: "");

            YtDlp.EffacerLaTrace();
            Mesures.Visibility = Visibility.Visible;
            // Un clic sur Annuler pendant la lecture de la playlist est honoré ici, avant de lancer le transfert.
            var code = _annulationDemandee
                ? -1
                : await LancerAsync(executable, YtDlp.ArgumentsTelechargement(demande, _complements), "Recherche de la vidéo…");
            var fichiers = _annulationDemandee ? [] : YtDlp.LireLaTrace();

            if (_annulationDemandee)
            {
                BarreProgression.Visibility = Visibility.Collapsed;
                MarquerLElementCourant("annulé");
                Terminer("ANNULÉ", "La partie déjà reçue reste dans le dossier, relancer le téléchargement le reprendra où il s'est arrêté.", Ton.Neutre);
            }
            else if (fichiers.Count > 0)
            {
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
                Resultat.Visibility = Visibility.Visible;
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
                Terminer("", "yt-dlp a terminé sans rien télécharger de nouveau, le détail est dans le journal.", Ton.Neutre);
            }
            else
            {
                BarreProgression.Visibility = Visibility.Collapsed;
                Mesures.Visibility = Visibility.Collapsed;
                MarquerLElementCourant("erreur");
                BasculeJournal.IsChecked = true;   // le détail de l'erreur est là, autant le montrer
                Terminer("ERREUR", $"yt-dlp a signalé une erreur (code {code}), le détail est dans le journal. "
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
                (string Nom, Func<IProgress<MiseAJourDesOutils.Avancement>, Task<string>> Tache)[] outils =
                [
                    ("ffmpeg", p => MiseAJourDesOutils.FfmpegAsync(client, p, arret.Token)),
                    ("Deno", p => MiseAJourDesOutils.DenoAsync(client, p, arret.Token)),
                    ("spotDL", p => MiseAJourDesOutils.SpotDlAsync(client, p, arret.Token)),
                ];
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

            if (annule)
            {
                Terminer("ANNULÉ", "Mise à jour interrompue. Les outils déjà traités restent à jour, les autres sont inchangés.", Ton.Neutre);
            }
            else if (echecs.Count == 0)
            {
                Terminer("À JOUR", "yt-dlp, ffmpeg, Deno et spotDL sont à leur dernière version.", Ton.Reussite);
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
        // Tous les yt-dlp en marche sont arrêtés, celui d'une vidéo seule comme les cinq d'une liste.
        // S'il n'y en a aucun (lecture d'un sommaire), la demande sera lue juste avant le lancement suivant.
        var enMarche = _actifs.Select(t => t.Processus).Append(_enCours).OfType<Process>().ToList();
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
        var enMarche = _actifs.Select(t => t.Processus).Append(_enCours).OfType<Process>().ToList();
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

    // Nombre de téléchargements menés en même temps dans un album ou une playlist.
    private const int Simultanes = 5;

    /// <summary>Un téléchargement d'une liste : sa ligne à l'écran, et les recherches à essayer dans l'ordre.</summary>
    private sealed class Travail(ElementDePlaylist element, string enCasDEchec, params Func<string, List<string>>[] tentatives)
    {
        public ElementDePlaylist Element { get; } = element;

        /// <summary>Le mot affiché si aucune tentative n'aboutit, « erreur » ou « introuvable ».</summary>
        public string EnCasDEchec { get; } = enCasDEchec;

        /// <summary>Chaque tentative reçoit son fichier de suivi et rend les arguments de yt-dlp. La première qui donne un fichier gagne.</summary>
        public Func<string, List<string>>[] Tentatives { get; } = tentatives;

        public Process? Processus;
        public double Recu, Total, Debit;   // le fichier en cours de transfert
        public double DejaRecu;             // les fichiers déjà finis de ce travail, l'image avant le son par exemple
        public bool DejaLa;
        public string Liaison = "—", Format = "—";
    }

    // Les travaux dont un yt-dlp tourne en ce moment. Tout se passe sur le fil de la fenêtre, sans verrou.
    private readonly List<Travail> _actifs = [];

    /// <summary>
    /// Mène une liste de téléchargements, plusieurs à la fois. Chaque « ouvrier » prend le travail suivant dans
    /// la file dès qu'il a fini le sien. Rend les fichiers obtenus et le nombre de travaux restés sans résultat.
    /// </summary>
    private async Task<(List<string> Fichiers, int Echecs)> MenerLaListeAsync(string executable, List<Travail> travaux)
    {
        _listeMaitrisee = true;
        _total = travaux.Count;
        var file = new Queue<Travail>(travaux);
        var fichiers = new List<string>();
        var echecs = 0;
        var faits = 0;
        var chrono = Stopwatch.StartNew();
        var deFront = Math.Min(Simultanes, travaux.Count);

        Mesures.Visibility = Visibility.Visible;
        BarreProgression.ClearValue(ForegroundProperty);
        BarreProgression.Value = 0;
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;
        BarreDesTaches.ProgressState = TaskbarItemProgressState.Indeterminate;
        if (_total > 1) BasculeElements.Visibility = Visibility.Visible;
        Etat(_total > 1 ? $"{_total} téléchargements, {deFront} à la fois" : "Recherche…", annoncer: true);

        void Montrer() => MontrerLEnsemble(travaux, faits, chrono.Elapsed.TotalSeconds);

        async Task Ouvrier(int numero)
        {
            // Un fichier de suivi par ouvrier, sinon cinq yt-dlp écriraient leur résultat au même endroit.
            var suivi = Path.Combine(YtDlp.DossierOutils, $"suivi-{numero}.txt");
            while (file.Count > 0 && !_annulationDemandee)
            {
                var travail = file.Dequeue();
                _actifs.Add(travail);
                travail.Element.Etat = "recherche";
                Montrer();

                string? fichier = null;
                foreach (var tentative in travail.Tentatives)
                {
                    if (_annulationDemandee) break;
                    YtDlp.EffacerLaTrace(suivi);
                    travail.DejaLa = false;
                    await LancerUnTravailAsync(executable, tentative(suivi), travail, Montrer);
                    if (YtDlp.LireLaTrace(suivi) is not { Count: > 0 } obtenus) continue;
                    fichier = obtenus[^1];
                    break;
                }
                YtDlp.EffacerLaTrace(suivi);
                _actifs.Remove(travail);
                travail.Recu = travail.Total = travail.Debit = 0;

                if (fichier is not null)
                {
                    fichiers.Add(fichier);
                    travail.Element.Etat = travail.DejaLa ? "déjà là" : "terminé";
                    travail.Element.Pourcent = 100;
                    faits++;
                }
                else if (_annulationDemandee)
                {
                    travail.Element.Etat = "annulé";
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
        return (fichiers, echecs);
    }

    private async Task LancerUnTravailAsync(string executable, List<string> arguments, Travail travail, Action montrer)
    {
        travail.Recu = travail.Total = travail.Debit = 0;
        void Lire(string ligne)
        {
            // Seules les lignes de mesure changent les chiffres d'ensemble, inutile de les recalculer pour les autres.
            if (RecevoirLigneDUnTravail(travail, ligne)) montrer();
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

        if (ligne.EndsWith("has already been downloaded", StringComparison.Ordinal)) travail.DejaLa = true;
        else if (EtapesDeFfmpeg.Any(etape => ligne.StartsWith(etape, StringComparison.Ordinal))) travail.Element.Etat = "assemblage";
        Ecrire(ligne, travail.Element.Numero);
        return false;
    }

    /// <summary>Les chiffres d'ensemble d'une liste : ce qui est fini, la somme des débits, le temps qui reste.</summary>
    private void MontrerLEnsemble(List<Travail> travaux, int faits, double secondes)
    {
        var total = travaux.Count;
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
            Resultat.Visibility = Visibility.Visible;
        }
        MesureDebit.Text = "—";

        if (_annulationDemandee)
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
            if (echecs > 0)
                Terminer("TERMINÉ", $"{fichiers.Count} reçus, {echecs} en échec. Le détail est dans la liste et dans le journal.", Ton.Erreur, motEnVert: true);
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
    }

    /// <summary>
    /// Plusieurs liens collés ensemble. Chacun devient un travail de la file, et un lien Spotify se déplie
    /// en autant de travaux qu'il a de morceaux. Le tout est mené de front comme une playlist.
    /// </summary>
    private async Task TelechargerPlusieursAsync(string executable, List<string> adresses, string dossier)
    {
        var audioSeul = ChoixAudio.IsChecked == true;
        var format = (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "";
        var qualite = (ChoixQualite.SelectedItem as Choix)?.Valeur ?? "";
        Ecrire("");
        Ecrire($"{adresses.Count} adresses à traiter.");
        Etat("Préparation de la file…", annoncer: true);
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;

        var travaux = new List<Travail>();
        using var client = MiseAJourDesOutils.NouveauClient();
        foreach (var adresse in adresses)
        {
            if (_annulationDemandee) break;
            if (!SpotDl.EstUneAdresseSpotify(adresse))
            {
                // Le titre affiché est l'adresse, le temps que yt-dlp donne le vrai titre.
                var element = new ElementDePlaylist(_elements.Count + 1) { Titre = adresse };
                _elements.Add(element);
                var demande = new Demande(adresse, audioSeul, format, qualite, dossier, PrefixeDeNom: "");
                travaux.Add(new Travail(element, "erreur", suivi => YtDlp.ArgumentsTelechargement(demande, _complements, suivi)));
                continue;
            }

            List<Morceau> morceaux = [];
            try
            {
                using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                (morceaux, _) = await PageSpotify.LireAsync(client, adresse, delai.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Le message ci-dessous couvre aussi ce cas.
            }
            if (morceaux.Count == 0)
            {
                Ecrire($"ERROR: le lien Spotify {adresse} n'a pas pu être lu, il est laissé de côté.");
                continue;
            }
            // Un lien Spotify ne donne que du son, en MP3 si l'on était sur Vidéo.
            var formatDuSon = audioSeul ? format : "mp3";
            var enListe = morceaux.Count > 1;
            foreach (var morceau in morceaux)
            {
                var element = new ElementDePlaylist(_elements.Count + 1) { Titre = $"{morceau.Artiste} - {morceau.Titre}" };
                _elements.Add(element);
                travaux.Add(new Travail(element, "introuvable",
                    suivi => YtDlp.ArgumentsPourUnMorceau(morceau, formatDuSon, dossier, enListe, _complements, surYoutubeMusic: true, suivi),
                    suivi => YtDlp.ArgumentsPourUnMorceau(morceau, formatDuSon, dossier, enListe, _complements, surYoutubeMusic: false, suivi)));
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

        // Un lien Spotify ne donne que du son, quel que soit le choix Vidéo ou Audio.
        var format = ChoixAudio.IsChecked == true ? (ChoixFormat.SelectedItem as Choix)?.Valeur ?? "mp3" : "mp3";
        Ecrire("");
        Ecrire($"Lecture du lien Spotify {adresse}");
        if (ChoixAudio.IsChecked != true) Ecrire("Un lien Spotify ne donne que du son, les fichiers seront en MP3.");

        // Premier temps, la description des morceaux. La source la plus rapide d'abord, la page publique de Spotify.
        Etat("Lecture du lien Spotify…", annoncer: true);
        BarreProgression.IsIndeterminate = true;
        BarreProgression.Visibility = Visibility.Visible;
        List<Morceau> morceaux = [];
        var incomplete = false;
        try
        {
            using var client = MiseAJourDesOutils.NouveauClient();
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            (morceaux, incomplete) = await PageSpotify.LireAsync(client, adresse, delai.Token);
            if (morceaux.Count > 0) Ecrire($"Lu sur la page publique de Spotify, {morceaux.Count} {(morceaux.Count > 1 ? "morceaux" : "morceau")}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Ecrire($"La page publique de Spotify n'a pas pu être lue. {ex.Message}");
        }

        // Recours, spotDL. Bien plus lent, mais il passe par le service officiel de Spotify et lit tout.
        if ((morceaux.Count == 0 || incomplete) && !_annulationDemandee && SpotDl.Trouver() is { } spotdl)
        {
            Ecrire(incomplete
                ? "Grande playlist. Lecture complète par spotDL, cela peut prendre plusieurs minutes."
                : "Lecture par spotDL, cela peut prendre plusieurs minutes.");
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
            Terminer("ERREUR", "Ce lien Spotify n'a pas pu être lu, le détail est dans le journal. "
                               + "Vérifiez que le lien est public, ou essayez le bouton Tout mettre à jour.", Ton.Erreur);
            return;
        }

        // Second temps, un téléchargement par morceau, plusieurs à la fois. Pour chacun, YouTube Music d'abord,
        // où se trouvent les enregistrements officiels, puis YouTube en second recours.
        Ecrire($"{morceaux.Count} {(morceaux.Count > 1 ? "morceaux à télécharger" : "morceau à télécharger")}.");
        var enListe = morceaux.Count > 1;
        var travaux = new List<Travail>();
        foreach (var morceau in morceaux)
        {
            var element = new ElementDePlaylist(_elements.Count + 1) { Titre = $"{morceau.Artiste} - {morceau.Titre}" };
            _elements.Add(element);
            travaux.Add(new Travail(element, "introuvable",
                suivi => YtDlp.ArgumentsPourUnMorceau(morceau, format, dossier, enListe, _complements, surYoutubeMusic: true, suivi),
                suivi => YtDlp.ArgumentsPourUnMorceau(morceau, format, dossier, enListe, _complements, surYoutubeMusic: false, suivi)));
        }

        var (fichiers, echecs) = await MenerLaListeAsync(executable, travaux);
        ConclureLaListe(fichiers, echecs);
    }

    /// <summary>Lignes de spotDL pendant la lecture du lien. Elles vont au journal, les erreurs en rouge.</summary>
    private void RecevoirLigneDeSpotDl(string brute)
    {
        var ligne = brute.Trim();
        if (ligne.Length == 0) return;
        Ecrire(ligne.Contains("Error", StringComparison.Ordinal) && !ligne.StartsWith("ERROR", StringComparison.Ordinal) ? "ERROR: " + ligne : ligne);
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
        Ecrire(ligne);
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
    private static string Octets(double octets) => octets switch
    {
        < 1024 * 1024 => string.Create(Francais, $"{octets / 1024:0} ko"),
        < 1024.0 * 1024 * 1024 => string.Create(Francais, $"{octets / (1024 * 1024):0.0} Mo"),
        _ => string.Create(Francais, $"{octets / (1024.0 * 1024 * 1024):0.00} Go"),
    };

    private static string Duree(double secondes)
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
        Ecrire($"[{rang:00}] {ligne}", enRouge: ligne.StartsWith("ERROR", StringComparison.Ordinal));

    private void Ecrire(string ligne, bool? enRouge = null)
    {
        var paragraphe = new Paragraph(new Run(ligne));
        if (enRouge ?? ligne.StartsWith("ERROR", StringComparison.Ordinal))
            paragraphe.Foreground = (Brush)FindResource("Alerte");
        var blocs = Journal.Document.Blocks;
        blocs.Add(paragraphe);
        while (blocs.Count > 3000) blocs.Remove(blocs.FirstBlock);   // un journal sans fin finirait par peser
        if (Journal.IsVisible) Journal.ScrollToEnd();
    }

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
        _dejaPresent = false;
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
        ZoneEtat.Visibility = TexteEtat.Text.Length > 0 || Afficheur.Text.Length > 0
                              || BarreProgression.Visibility == Visibility.Visible || Mesures.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static void Annoncer(UIElement element) =>
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

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
