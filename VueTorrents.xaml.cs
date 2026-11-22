using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
#if PORTABLE
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
#endif

namespace YtDlpInterface;

/// <summary>
/// La vue des torrents de la fenêtre principale. Elle tient son propre aria2, indépendant des autres
/// téléchargements : un torrent continue d'avancer quand on revient sur la vue de téléchargement, et la
/// fenêtre reste libre pour autre chose. Les torrents s'arrêtent quand l'application se ferme.
/// </summary>
public partial class VueTorrents : UserControl
{
    private readonly ServiceTorrents _service = new();
    private readonly ObservableCollection<LigneDeTorrent> _torrents = [];
    private readonly ObservableCollection<LigneDeFichier> _fichiers = [];
    private readonly ObservableCollection<LigneDePair> _pairs = [];
    private readonly DispatcherTimer _minuterie = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<string> _dossier;
    private bool _rafraichissementEnCours;
    private int _enCours = -1;

    /// <summary>Prévient la fenêtre quand le nombre de torrents en cours change, pour l'écrire sur la touche « Torrents ».</summary>
    public event Action<int>? EnCoursChange;

    /// <param name="habillage">La palette et les styles de la fenêtre principale, repris tels quels.</param>
    /// <param name="dossier">Rend le dossier de destination choisi dans la fenêtre, au moment où on le demande.</param>
#if PORTABLE
    public VueTorrents(Func<string> dossier)
    {
        InitializeComponent();
#else
    public VueTorrents(ResourceDictionary habillage, Func<string> dossier)
    {
        // Avant de lire le dessin : il fait appel à ces styles dès son chargement, alors que la vue
        // n'est pas encore posée dans la fenêtre et ne peut donc pas les trouver par elle-même.
        Resources.MergedDictionaries.Add(habillage);
        InitializeComponent();
#endif
        _dossier = dossier;
        Liste.ItemsSource = _torrents;
        Fichiers.ItemsSource = _fichiers;
        Pairs.ItemsSource = _pairs;
        _minuterie.Tick += async (_, _) => await RafraichirAsync();
#if !PORTABLE
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            MontrerLeRangement();
            ChampTorrent.Focus();
        };
#endif
        MontrerLeRangement();
    }

#if PORTABLE
    // Avalonia prévient d'un changement de visibilité par cette méthode, là où WPF a un événement.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs changement)
    {
        base.OnPropertyChanged(changement);
        if (changement.Property != IsVisibleProperty || !IsVisible || TexteRangement is null) return;
        MontrerLeRangement();
        ChampTorrent.Focus();
    }
#endif

    /// <summary>Fermeture de l'application : aria2 est arrêté, ce qui est déjà reçu reste sur le disque.</summary>
    public void Arreter()
    {
        _minuterie.Stop();
        _service.Arreter();
    }

    private void MontrerLeRangement()
    {
        TexteRangement.Text = $"Rangés dans {_dossier()}";
        TexteRangement.ToolTip = "Le dossier choisi dans la vue de téléchargement, au moment où le torrent est ajouté.";
    }

    private void Dire(string message, bool erreur = false)
    {
        TexteMessage.Text = message;
        TexteMessage.SetResourceReference(TextBlock.ForegroundProperty, erreur ? "Alerte" : "TexteDoux");
    }

    // ───────────────────────────── Ajouter ─────────────────────────────

    private void ValiderAuClavier(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AjouterDepuisLeChamp(sender, e);
    }

    private async void AjouterDepuisLeChamp(object sender, RoutedEventArgs e)
    {
        var torrents = YtDlp.AdressesValides(ChampTorrent.Text).Where(Aria2.EstUnTorrent).ToList();
        if (torrents.Count == 0)
        {
            Dire("Collez un lien magnet (il commence par magnet:) ou l'adresse d'un fichier .torrent.", erreur: true);
            ChampTorrent.Focus();
            return;
        }
        ChampTorrent.Clear();
        foreach (var torrent in torrents) await AjouterAsync(torrent, _dossier());
    }

    /// <summary>Ajoute un torrent à la liste et le lance. Rend vrai s'il a été accepté.</summary>
    public async Task<bool> AjouterAsync(string adresse, string dossier)
    {
        var nom = Aria2.NomCourt(adresse);
        try
        {
            Dire($"Lecture de {nom}…");
            Directory.CreateDirectory(dossier);
            await _service.DemarrerAsync();
            using var client = MiseAJourDesOutils.NouveauClient();
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (magnet, fichier) = await Aria2.LireLaSourceAsync(adresse, client, delai.Token);
            await _service.AjouterAsync(magnet, fichier, dossier);
            Dire($"Ajouté : {nom}. Rien n'est partagé une fois le torrent terminé.");
            _minuterie.Start();
            await RafraichirAsync();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      or OperationCanceledException or InvalidOperationException or Win32Exception or System.Text.Json.JsonException)
        {
            // Le cas courant : le même torrent est déjà dans la liste, aria2 le refuse.
            Dire(e.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase)
                ? $"{nom} est déjà dans la liste."
                : $"{nom} n'a pas pu être ajouté. {e.Message}", erreur: true);
            return false;
        }
    }

    // ───────────────────────────── Suivre ─────────────────────────────

    /// <summary>Relit l'état de tous les torrents, une fois par seconde, et met à jour les lignes qui ont changé.</summary>
    private async Task RafraichirAsync()
    {
        if (_rafraichissementEnCours || !_service.EnMarche) return;
        _rafraichissementEnCours = true;
        try
        {
            var lus = await _service.ListerAsync();
            // La lecture d'un lien magnet est un premier « téléchargement » qu'aria2 remplace ensuite par le vrai.
            // Une fois remplacée, elle n'a plus rien à dire : on l'oublie sans la montrer.
            static bool Remplacee(TorrentLu t) => t.Suivi && t.Statut is "complete" or "removed";
            foreach (var lecture in lus.Where(Remplacee))
            {
                try { await _service.RetirerAsync(lecture.Gid, enCours: false); }
                catch (InvalidOperationException) { }
            }
            var visibles = lus.Where(t => !Remplacee(t)).ToList();

            foreach (var disparu in _torrents.Where(l => visibles.All(t => t.Gid != l.Gid)).ToList()) _torrents.Remove(disparu);
            foreach (var t in visibles)
            {
                var ligne = _torrents.FirstOrDefault(l => l.Gid == t.Gid);
                if (ligne is null) _torrents.Add(ligne = new LigneDeTorrent(t.Gid));
                var avant = ligne.Etat;
                Remplir(ligne, t);
                if (avant.Length > 0 && avant != "terminé" && ligne.Etat == "terminé") Dire($"Terminé : {ligne.Nom}");
            }
            TexteVide.Visibility = _torrents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Le premier torrent ajouté est choisi d'office, pour que le détail ne reste pas vide.
            if (Liste.SelectedItem is null && _torrents.Count > 0) Liste.SelectedIndex = 0;

            var actifs = visibles.Count(t => t.Statut == "active");
            TexteEnsemble.Text = actifs > 0
                ? $"reçoit {FenetrePrincipale.Octets(visibles.Sum(t => t.Debit))}/s     envoie {FenetrePrincipale.Octets(visibles.Sum(t => t.Envoi))}/s     aria2 {_service.Version}"
                : _service.Version.Length > 0 ? $"aria2 {_service.Version}" : "";

            // Ce qui n'est ni fini ni arrêté compte comme « en cours » pour la touche de la fenêtre.
            var enCours = visibles.Count(t => t.Statut is "active" or "waiting" or "paused");
            if (enCours != _enCours) EnCoursChange?.Invoke(_enCours = enCours);

            await MontrerLeDetailAsync(visibles);
            // Plus rien ne bouge : inutile d'interroger aria2 chaque seconde. Un ajout ou une reprise relance la minuterie.
            if (actifs == 0 && visibles.All(t => t.Statut != "waiting")) _minuterie.Stop();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException
                                      or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // aria2 n'a pas répondu à ce tour, le suivant réessaiera. La raison est dite, sans quoi la liste resterait muette.
            Dire($"aria2 n'a pas répondu. {e.Message}", erreur: true);
        }
        finally
        {
            _rafraichissementEnCours = false;
        }
    }

    private static void Remplir(LigneDeTorrent ligne, TorrentLu t)
    {
        var part = t.Total > 0 ? Math.Clamp(t.Recu / t.Total, 0, 1) : 0;
        ligne.Statut = t.Statut;
        ligne.Dossier = t.Dossier;
        ligne.Erreur = t.Erreur;
        ligne.PremierFichier = t.Fichiers.Count > 0 && !t.EstUneLecture ? t.Fichiers[0].Chemin.Replace('/', Path.DirectorySeparatorChar) : "";
        ligne.Nom = t.Nom;
        ligne.Etat = t.Statut switch
        {
            "active" when t.EstUneLecture => "lecture du lien magnet",
            "active" when t.Total > 0 && t.Recu >= t.Total => "vérification",
            "active" when t.Debit > 0 => "en cours",
            "active" => t.Liaisons > 0 ? "en attente d'une source" : "recherche de sources",
            "waiting" => "en file",
            "paused" => "en pause",
            "complete" => "terminé",
            "error" => "erreur",
            _ => t.Statut,
        };
        ligne.Pourcent = t.Statut == "complete" ? 100 : part * 100;
        // En cours mais sans rien recevoir : la barre défile, pour qu'on voie que le torrent cherche encore.
        ligne.SansMouvement = t.Statut == "active" && t.Debit <= 0 && part < 1;
        ligne.Recu = t.Total > 0 ? $"{FenetrePrincipale.Octets(t.Recu)} / {FenetrePrincipale.Octets(t.Total)}" : "";
        var actif = t.Statut == "active";
        ligne.Debit = actif && t.Debit > 0 ? $"{FenetrePrincipale.Octets(t.Debit)}/s" : "—";
        ligne.Envoi = actif && t.Envoi > 0 ? $"{FenetrePrincipale.Octets(t.Envoi)}/s" : "—";
        // aria2 compte les pairs reliés, dont ceux qui ont tout (les sources). Les autres téléchargent encore.
        ligne.Sources = actif ? t.Sources.ToString() : "—";
        ligne.Pairs = actif ? Math.Max(0, t.Liaisons - t.Sources).ToString() : "—";
        ligne.Reste = actif && t.Debit > 0 && t.Total > t.Recu ? FenetrePrincipale.Duree((t.Total - t.Recu) / t.Debit) : "—";
    }

    private async void ChoisirUnTorrent(object sender, SelectionChangedEventArgs e)
    {
        _fichiers.Clear();
        _pairs.Clear();
        MontrerLesBoutons();
        if (!_service.EnMarche || _rafraichissementEnCours) return;
        try
        {
            await MontrerLeDetailAsync(await _service.ListerAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Le prochain tour de la minuterie remplira le détail.
        }
    }

    private void MontrerLesBoutons()
    {
        var choisi = Liste.SelectedItem as LigneDeTorrent;
        BoutonPause.IsEnabled = choisi?.Statut is "active" or "waiting" or "paused";
        BoutonPause.Content = choisi?.Statut == "paused" ? "_Reprendre" : "_Pause";
        BoutonRetirer.IsEnabled = choisi is not null;
        BoutonDossier.IsEnabled = choisi is not null;
        if (choisi is { Etat: "erreur", Erreur.Length: > 0 }) Dire($"{choisi.Nom} s'est arrêté sur une erreur. {choisi.Erreur}", erreur: true);
    }

    /// <summary>Les fichiers et les pairs du torrent choisi.</summary>
    private async Task MontrerLeDetailAsync(List<TorrentLu> lus)
    {
        MontrerLesBoutons();
        if (Liste.SelectedItem is not LigneDeTorrent choisi || lus.FirstOrDefault(t => t.Gid == choisi.Gid) is not { } torrent)
        {
            _fichiers.Clear();
            _pairs.Clear();
            TitreFichiers.Text = "FICHIERS";
            TitrePairs.Text = "PAIRS RELIÉS";
            return;
        }

        // Au-delà de quelques centaines de lignes une liste n'aide plus personne.
        List<FichierLu> fichiers = torrent.EstUneLecture ? [] : torrent.Fichiers.Take(500).ToList();
        while (_fichiers.Count > fichiers.Count) _fichiers.RemoveAt(_fichiers.Count - 1);
        while (_fichiers.Count < fichiers.Count) _fichiers.Add(new LigneDeFichier());
        var racine = torrent.Dossier.Replace('\\', '/').TrimEnd('/') + "/";
        for (var i = 0; i < fichiers.Count; i++)
        {
            var chemin = fichiers[i].Chemin;
            // Le chemin est montré à partir du dossier de rangement, le début n'apprend rien.
            _fichiers[i].Nom = chemin.StartsWith(racine, StringComparison.OrdinalIgnoreCase) ? chemin[racine.Length..] : chemin;
            _fichiers[i].Part = fichiers[i].Taille > 0 ? $"{Math.Floor(100 * fichiers[i].Recu / fichiers[i].Taille):0} %" : "—";
            _fichiers[i].Taille = FenetrePrincipale.Octets(fichiers[i].Taille);
        }
        TitreFichiers.Text = fichiers.Count > 1 ? $"{torrent.Fichiers.Count} FICHIERS" : "FICHIERS";

        List<PairLu> pairs = torrent.Statut == "active" && !torrent.EstUneLecture ? await _service.PairsAsync(torrent.Gid) : [];
        pairs = pairs.OrderByDescending(p => p.Recoit).ThenByDescending(p => p.Source).Take(200).ToList();
        while (_pairs.Count > pairs.Count) _pairs.RemoveAt(_pairs.Count - 1);
        while (_pairs.Count < pairs.Count) _pairs.Add(new LigneDePair());
        for (var i = 0; i < pairs.Count; i++)
        {
            _pairs[i].Adresse = pairs[i].Adresse;
            _pairs[i].Logiciel = pairs[i].Logiciel;
            _pairs[i].Recoit = pairs[i].Recoit > 0 ? $"{FenetrePrincipale.Octets(pairs[i].Recoit)}/s" : "—";
            _pairs[i].Envoie = pairs[i].Envoie > 0 ? $"{FenetrePrincipale.Octets(pairs[i].Envoie)}/s" : "—";
            _pairs[i].Role = pairs[i].Source ? "source" : "pair";
        }
        TitrePairs.Text = pairs.Count > 1 ? $"{pairs.Count} PAIRS RELIÉS" : pairs.Count == 1 ? "1 PAIR RELIÉ" : "PAIRS RELIÉS";
    }

    // ───────────────────────────── Agir sur le torrent choisi ─────────────────────────────

    private async void MettreEnPauseOuReprendre(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not LigneDeTorrent choisi) return;
        try
        {
            if (choisi.Statut == "paused") await _service.ReprendreAsync(choisi.Gid);
            else await _service.PauseAsync(choisi.Gid);
            _minuterie.Start();
            await RafraichirAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            Dire($"aria2 n'a pas pu changer l'état de ce torrent. {ex.Message}", erreur: true);
        }
    }

    private async void Retirer(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not LigneDeTorrent choisi) return;
        try
        {
            var enCours = choisi.Statut is "active" or "waiting" or "paused";
            await _service.RetirerAsync(choisi.Gid, enCours);
            Dire(enCours
                ? $"{choisi.Nom} est arrêté et retiré de la liste. Ce qui était déjà reçu reste dans le dossier."
                : $"{choisi.Nom} est retiré de la liste. Ses fichiers restent dans le dossier.");
            _minuterie.Start();
            await RafraichirAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            Dire($"Ce torrent n'a pas pu être retiré. {ex.Message}", erreur: true);
        }
    }

#if PORTABLE
    private void OuvrirLeDossier(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not LigneDeTorrent choisi) return;
        try
        {
            // Le dossier du premier fichier s'il existe déjà, sinon le dossier de rangement.
            var dossier = choisi.PremierFichier.Length > 0 && Path.GetDirectoryName(choisi.PremierFichier) is { Length: > 0 } premier && Directory.Exists(premier)
                ? premier
                : choisi.Dossier.Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(dossier)) Plateforme.Ouvrir(dossier);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Dire($"Impossible d'ouvrir le dossier. {ex.Message}", erreur: true);
        }
    }
#else
    private void OuvrirLeDossier(object sender, RoutedEventArgs e)
    {
        if (Liste.SelectedItem is not LigneDeTorrent choisi) return;
        try
        {
            // Chemin complet de l'Explorateur de Windows. Un nom nu pourrait désigner un fichier du même nom posé ailleurs.
            var explorateur = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var demarrage = new ProcessStartInfo(explorateur) { UseShellExecute = false };
            // Le premier fichier du torrent est montré dans son dossier s'il existe déjà, sinon le dossier de rangement est ouvert.
            if (choisi.PremierFichier.Length > 0 && File.Exists(choisi.PremierFichier)) demarrage.Arguments = $"/select,\"{choisi.PremierFichier}\"";
            else if (Directory.Exists(choisi.Dossier)) demarrage.ArgumentList.Add(choisi.Dossier.Replace('/', Path.DirectorySeparatorChar));
            else return;
            Process.Start(demarrage)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Dire($"Impossible d'ouvrir l'Explorateur. {ex.Message}", erreur: true);
        }
    }
#endif
}
