using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YtDlpInterface;

// Les lignes que montre la vue des torrents. Elles ne dépendent d'aucune bibliothèque d'interface,
// la version Windows (WPF) et la version Linux (Avalonia) se servent des mêmes.
/// <summary>Base des lignes affichées : prévient l'écran quand une valeur change, et seulement dans ce cas.</summary>
internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Prevenir(string nom) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nom));

    protected void Changer<T>(ref T champ, T valeur, [CallerMemberName] string nom = "")
    {
        if (EqualityComparer<T>.Default.Equals(champ, valeur)) return;
        champ = valeur;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nom));
    }
}

/// <summary>Un torrent, tel que la liste le montre.</summary>
internal sealed class LigneDeTorrent(string gid) : Observable
{
    private string _nom = "", _etat = "", _recu = "", _debit = "—", _envoi = "—", _sources = "—", _pairs = "—", _reste = "—";
    private double _pourcent;
    private bool _sansMouvement;

    public string Gid { get; } = gid;
    public string Nom { get => _nom; set => Changer(ref _nom, value); }
#if PORTABLE
    public string Etat
    {
        get => _etat;
        set
        {
            if (_etat == value) return;
            Changer(ref _etat, value);
            // Avalonia n'a pas les déclencheurs de WPF : la couleur de la barre suit ces trois valeurs.
            Prevenir(nameof(Fini));
            Prevenir(nameof(EnEchec));
            Prevenir(nameof(EnPause));
        }
    }

    public bool Fini => _etat == "terminé";

    public bool EnEchec => _etat == "erreur";

    public bool EnPause => _etat == "en pause";
#else
    public string Etat { get => _etat; set => Changer(ref _etat, value); }
#endif
    public double Pourcent { get => _pourcent; set => Changer(ref _pourcent, value); }

    /// <summary>Vrai quand le torrent est en cours mais que rien n'arrive : la barre défile au lieu de rester immobile.</summary>
    public bool SansMouvement { get => _sansMouvement; set => Changer(ref _sansMouvement, value); }
    public string Recu { get => _recu; set => Changer(ref _recu, value); }
    public string Debit { get => _debit; set => Changer(ref _debit, value); }
    public string Envoi { get => _envoi; set => Changer(ref _envoi, value); }
    public string Sources { get => _sources; set => Changer(ref _sources, value); }
    public string Pairs { get => _pairs; set => Changer(ref _pairs, value); }
    public string Reste { get => _reste; set => Changer(ref _reste, value); }

    // Ce que l'écran ne montre pas mais dont les boutons ont besoin.
    public string Statut = "";
    public string Dossier = "";
    public string PremierFichier = "";
    public string Erreur = "";
}

/// <summary>Un fichier du torrent choisi.</summary>
internal sealed class LigneDeFichier : Observable
{
    private string _nom = "", _part = "", _taille = "";
    public string Nom { get => _nom; set => Changer(ref _nom, value); }
    public string Part { get => _part; set => Changer(ref _part, value); }
    public string Taille { get => _taille; set => Changer(ref _taille, value); }
}

/// <summary>Un pair relié au torrent choisi.</summary>
internal sealed class LigneDePair : Observable
{
    private string _adresse = "", _logiciel = "", _recoit = "", _envoie = "", _role = "";
    public string Adresse { get => _adresse; set => Changer(ref _adresse, value); }
    public string Logiciel { get => _logiciel; set => Changer(ref _logiciel, value); }
    public string Recoit { get => _recoit; set => Changer(ref _recoit, value); }
    public string Envoie { get => _envoie; set => Changer(ref _envoie, value); }
    public string Role { get => _role; set => Changer(ref _role, value); }
}
