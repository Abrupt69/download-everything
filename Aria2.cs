using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtDlpInterface;

/// <summary>
/// aria2, l'outil des torrents. Ni yt-dlp ni gallery-dl ne parlent BitTorrent, c'est donc lui qui prend
/// les liens magnet et les fichiers .torrent. Cette classe dit ce qu'est un torrent et prépare sa source.
/// Le pilotage d'aria2 pendant qu'il travaille est dans <see cref="ServiceTorrents"/>.
/// </summary>
internal static class Aria2
{
#if PORTABLE
    public static string NomExecutable => Plateforme.Outil("aria2c");

    // Sous Linux aria2 vient du système, installé avec le paquet. Le dossier des outils garde la priorité.
    public static string? Trouver()
    {
        var chemin = Path.Combine(YtDlp.DossierOutils, NomExecutable);
        return File.Exists(chemin) ? chemin : Plateforme.DansLeSysteme("aria2c");
    }
#else
    public const string NomExecutable = "aria2c.exe";

    public static string? Trouver()
    {
        var chemin = Path.Combine(YtDlp.DossierOutils, NomExecutable);
        return File.Exists(chemin) ? chemin : null;
    }
#endif

    /// <summary>
    /// Vrai pour les trois formes qu'un torrent peut prendre dans le champ : un lien magnet, l'adresse web
    /// d'un fichier .torrent, ou un fichier .torrent du PC (écrit « file:///… », la forme sans espace que
    /// lui donne le glisser-déposer).
    /// </summary>
    public static bool EstUnTorrent(string adresse)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme switch
        {
            "magnet" => adresse.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase),
            "file" => uri.LocalPath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase),
            "http" or "https" => uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Ce qu'on écrit pour désigner un torrent : son nom s'il est dans le lien, sinon le nom du fichier.</summary>
    public static string NomCourt(string adresse)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return adresse;
        if (uri.Scheme == "file") return Path.GetFileName(uri.LocalPath);
        if (uri.Scheme == "magnet")
        {
            // Le nom est porté par le paramètre « dn » du lien magnet, quand il y est.
            var nom = Regex.Match(adresse, @"[?&]dn=([^&]+)").Groups[1].Value;
            return nom.Length > 0 ? Uri.UnescapeDataString(nom.Replace('+', ' ')) : "lien magnet";
        }
        return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
    }

    /// <summary>
    /// Rend ce qu'il faut donner à aria2 : le lien magnet tel quel, ou le contenu d'un fichier .torrent.
    /// Un .torrent désigné par une adresse web est lu ici, en mémoire, pour qu'il ne vienne pas se poser
    /// au milieu des téléchargements.
    /// </summary>
    public static async Task<(string? Magnet, byte[]? Fichier)> LireLaSourceAsync(string adresse, HttpClient client, CancellationToken arret)
    {
        var uri = new Uri(adresse);
        if (uri.Scheme == "magnet") return (adresse, null);

        byte[] octets;
        if (uri.Scheme == "file")
        {
            if (!File.Exists(uri.LocalPath)) throw new FileNotFoundException("Ce fichier .torrent n'est plus à cet endroit.", uri.LocalPath);
            if (new FileInfo(uri.LocalPath).Length > TailleMaximale) throw new InvalidDataException("Ce fichier est trop gros pour être un fichier .torrent.");
            octets = await File.ReadAllBytesAsync(uri.LocalPath, arret);
        }
        else
        {
            using var reponse = await client.GetAsync(adresse, HttpCompletionOption.ResponseHeadersRead, arret);
            reponse.EnsureSuccessStatusCode();
            if (reponse.Content.Headers.ContentLength > TailleMaximale) throw new InvalidDataException("Ce fichier est trop gros pour être un fichier .torrent.");
            octets = await reponse.Content.ReadAsByteArrayAsync(arret);
        }
        // Un .torrent est un dictionnaire au format « bencode », il commence toujours par la lettre d.
        if (octets.Length < 20 || octets.Length > TailleMaximale || octets[0] != (byte)'d')
            throw new InvalidDataException("Ce n'est pas un fichier .torrent valide.");
        return (null, octets);
    }

    // Un fichier .torrent pèse quelques dizaines de ko. Au-delà de 20 Mo ce n'en est pas un.
    private const int TailleMaximale = 20 << 20;

    public static ProcessStartInfo Preparer(string executable, IEnumerable<string> arguments)
    {
        var demarrage = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = YtDlp.DossierOutils,
        };
        foreach (var argument in arguments) demarrage.ArgumentList.Add(argument);
        return demarrage;
    }
}

/// <summary>Un fichier d'un torrent, tel qu'aria2 le décrit.</summary>
internal sealed record FichierLu(string Chemin, double Taille, double Recu);

/// <summary>L'état d'un torrent à un instant donné.</summary>
internal sealed record TorrentLu(
    string Gid, string Statut, string Nom, double Total, double Recu, double Debit, double Envoi,
    int Liaisons, int Sources, string Dossier, string Erreur, bool EstUneLecture, bool Suivi, IReadOnlyList<FichierLu> Fichiers);

/// <summary>Un pair relié à un torrent : son adresse, son logiciel, ce qu'il nous envoie et ce qu'on lui envoie.</summary>
internal sealed record PairLu(string Adresse, string Logiciel, double Recoit, double Envoie, bool Source);

/// <summary>
/// Fait tourner aria2 en service et lui parle. Un torrent dure, et l'on veut voir ses sources, ses pairs et
/// ses fichiers pendant qu'il avance : aria2 est donc lancé une fois, en arrière-plan, et interrogé chaque
/// seconde par son interface de commande (JSON-RPC), ouverte sur ce seul PC et protégée par un mot de passe
/// tiré au hasard à chaque lancement.
/// </summary>
internal sealed class ServiceTorrents
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Process? _aria2;
    private int _port;
    private string _secret = "";

    public bool EnMarche => _aria2 is { HasExited: false };

    public string Version { get; private set; } = "";

    /// <summary>Lance aria2 s'il ne tourne pas déjà, et attend qu'il réponde.</summary>
    public async Task DemarrerAsync()
    {
        if (EnMarche) return;
        var executable = Aria2.Trouver();
        if (executable is null)
        {
#if PORTABLE
            await Task.CompletedTask;
            throw new FileNotFoundException("aria2, l'outil des torrents, n'est pas installé. Sous Debian ou Ubuntu : sudo apt install aria2");
#else
            // L'outil a été retiré du dossier : la charge embarquée le redépose, sans toucher aux autres.
            await Task.Run(() => ChargeEmbarquee.Deployer());
            executable = Aria2.Trouver() ?? throw new FileNotFoundException("aria2, l'outil des torrents, manque dans le dossier des outils.");
#endif
        }

        _port = PortLibre();
        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var processus = new Process { StartInfo = Aria2.Preparer(executable, Arguments(_port, _secret)) };
        // Les deux canaux sont vidés au fur et à mesure, sans quoi aria2 finirait par se bloquer en écrivant.
        processus.OutputDataReceived += (_, _) => { };
        processus.ErrorDataReceived += (_, _) => { };
        processus.Start();
        processus.BeginOutputReadLine();
        processus.BeginErrorReadLine();
        _aria2 = processus;

        for (var essai = 0; essai < 60; essai++)
        {
            try
            {
                var version = await AppelerAsync("aria2.getVersion");
                Version = version.GetProperty("version").GetString() ?? "";
                return;
            }
            catch (HttpRequestException)
            {
                if (processus.HasExited) break;
                await Task.Delay(150);
            }
        }
        Arreter();
        throw new InvalidOperationException("aria2 n'a pas démarré. L'antivirus bloque peut-être son lancement.");
    }

    private static List<string> Arguments(int port, string secret) =>
    [
        "--no-conf=true",                 // aucun fichier de réglages du poste, même comportement chez tout le monde
        "--enable-rpc=true",
        "--rpc-listen-all=false",         // l'interface de commande n'écoute que ce PC
        $"--rpc-listen-port={port}",
        $"--rpc-secret={secret}",
        // Si l'application disparaît sans avoir pu l'arrêter, aria2 s'arrête de lui-même.
        $"--stop-with-process={Environment.ProcessId}",
        "--seed-time=0",                  // rien n'est partagé une fois un torrent fini
        "--file-allocation=none",         // pas de réservation de place préalable, qui fige plusieurs minutes sur un gros fichier
        "--bt-save-metadata=false",
        "--rpc-save-upload-metadata=false",   // sinon aria2 pose une copie du fichier .torrent au milieu des téléchargements
        // Mesuré le 22/11/2026 : avec les délais d'origine (une minute), une seule source web muette laisse le
        // torrent immobile près de deux minutes. Ils sont raccourcis, et plusieurs sources sont essayées de front.
        "--connect-timeout=10",
        "--timeout=20",
        "--max-tries=2",
        "--retry-wait=1",
        "--split=8",
        // Un traqueur muet est abandonné en dix secondes au lieu d'une minute (mesuré le 22/11/2026 sur celui de Debian).
        "--bt-tracker-connect-timeout=10",
        "--bt-tracker-timeout=15",
        // Porte d'entrée du réseau de pairs (DHT), pour les liens magnet qui ne nomment aucun traqueur. Sans elle,
        // à la première utilisation, aria2 ne connaît personne à qui demander.
        "--dht-entry-point=dht.transmissionbt.com:6881",
        "--max-concurrent-downloads=5",
        "--enable-color=false",
        "--console-log-level=warn",
        "--summary-interval=0",
        // Le carnet d'adresses du réseau de pairs, gardé avec les outils plutôt que dans le profil de l'utilisateur.
        $"--dht-file-path={Path.Combine(YtDlp.DossierOutils, "aria2-dht.dat")}",
        $"--dht-file-path6={Path.Combine(YtDlp.DossierOutils, "aria2-dht6.dat")}",
    ];

    private static int PortLibre()
    {
        // Windows choisit un port inoccupé, on le relâche aussitôt pour le donner à aria2.
        var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        var port = ((IPEndPoint)ecoute.LocalEndpoint).Port;
        ecoute.Stop();
        return port;
    }

    private async Task<JsonElement> AppelerAsync(string methode, params object[] parametres)
    {
        var demande = new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "de",
            ["method"] = methode,
            ["params"] = new object[] { "token:" + _secret }.Concat(parametres).ToArray(),
        };
        using var corps = new StringContent(JsonSerializer.Serialize(demande), Encoding.UTF8, "application/json");
        using var reponse = await _client.PostAsync($"http://127.0.0.1:{_port}/jsonrpc", corps);
        using var document = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync());
        if (document.RootElement.TryGetProperty("error", out var erreur))
            throw new InvalidOperationException(erreur.TryGetProperty("message", out var message) ? message.GetString() : "aria2 a refusé la demande.");
        return document.RootElement.GetProperty("result").Clone();
    }

    /// <summary>Confie un torrent à aria2, à ranger dans le dossier donné.</summary>
    public async Task AjouterAsync(string? magnet, byte[]? fichier, string dossier)
    {
        var options = new Dictionary<string, string> { ["dir"] = dossier };
        if (magnet is not null) await AppelerAsync("aria2.addUri", new[] { magnet }, options);
        else await AppelerAsync("aria2.addTorrent", Convert.ToBase64String(fichier!), Array.Empty<string>(), options);
    }

    private static readonly string[] Champs =
    [
        "gid", "status", "totalLength", "completedLength", "downloadSpeed", "uploadSpeed", "connections", "numSeeders",
        "dir", "files", "bittorrent", "errorMessage", "followedBy",
    ];

    /// <summary>Tous les torrents connus d'aria2 : ceux qui avancent, ceux qui attendent, ceux qui sont finis ou arrêtés.</summary>
    public async Task<List<TorrentLu>> ListerAsync()
    {
        var torrents = new List<TorrentLu>();
        foreach (var groupe in new[]
                 {
                     // « (object) » : sans lui, la liste des champs serait prise pour la liste des paramètres
                     // elle-même, chaque champ devenant un paramètre à part, et aria2 refuserait la demande.
                     await AppelerAsync("aria2.tellActive", (object)Champs),
                     await AppelerAsync("aria2.tellWaiting", 0, 200, Champs),
                     await AppelerAsync("aria2.tellStopped", 0, 200, Champs),
                 })
        {
            foreach (var t in groupe.EnumerateArray()) torrents.Add(Lire(t));
        }
        return torrents;
    }

    private static TorrentLu Lire(JsonElement t)
    {
        var fichiers = new List<FichierLu>();
        if (t.TryGetProperty("files", out var liste))
        {
            foreach (var f in liste.EnumerateArray())
                fichiers.Add(new FichierLu(Texte(f, "path"), Nombre(f, "length"), Nombre(f, "completedLength")));
        }

        // Pendant la lecture d'un lien magnet, aria2 ne connaît pas encore le contenu : son seul « fichier » s'appelle [METADATA].
        var lecture = fichiers.Count > 0 && fichiers[0].Chemin.StartsWith("[METADATA]", StringComparison.Ordinal);
        var nom = "";
        if (t.TryGetProperty("bittorrent", out var bt) && bt.TryGetProperty("info", out var info)) nom = Texte(info, "name");
        if (nom.Length == 0 && fichiers.Count > 0)
            nom = lecture ? fichiers[0].Chemin["[METADATA]".Length..] : Path.GetFileName(fichiers[0].Chemin.Replace('/', Path.DirectorySeparatorChar));
        if (nom.Length == 0) nom = "Torrent";

        var suivi = t.TryGetProperty("followedBy", out var suite) && suite.ValueKind == JsonValueKind.Array && suite.GetArrayLength() > 0;
        return new TorrentLu(Texte(t, "gid"), Texte(t, "status"), nom, Nombre(t, "totalLength"), Nombre(t, "completedLength"),
            Nombre(t, "downloadSpeed"), Nombre(t, "uploadSpeed"), (int)Nombre(t, "connections"), (int)Nombre(t, "numSeeders"),
            Texte(t, "dir"), Texte(t, "errorMessage"), lecture, suivi, fichiers);
    }

    /// <summary>Les pairs reliés à un torrent en ce moment. Vide pour un torrent qui n'est pas en cours.</summary>
    public async Task<List<PairLu>> PairsAsync(string gid)
    {
        var pairs = new List<PairLu>();
        foreach (var p in (await AppelerAsync("aria2.getPeers", gid)).EnumerateArray())
        {
            pairs.Add(new PairLu($"{Texte(p, "ip")}:{Texte(p, "port")}", Logiciel(Texte(p, "peerId")),
                Nombre(p, "downloadSpeed"), Nombre(p, "uploadSpeed"), Texte(p, "seeder") == "true"));
        }
        return pairs;
    }

    public Task PauseAsync(string gid) => AppelerAsync("aria2.forcePause", gid);

    public Task ReprendreAsync(string gid) => AppelerAsync("aria2.unpause", gid);

    /// <summary>Retire un torrent de la liste. S'il était en cours il est arrêté, ses fichiers déjà reçus restent sur le disque.</summary>
    public async Task RetirerAsync(string gid, bool enCours)
    {
        if (enCours)
        {
            await AppelerAsync("aria2.forceRemove", gid);
            // L'arrêt prend un instant, le résultat ne peut être oublié qu'une fois le torrent vraiment arrêté.
            for (var essai = 0; essai < 20; essai++)
            {
                await Task.Delay(150);
                try
                {
                    await AppelerAsync("aria2.removeDownloadResult", gid);
                    return;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        else await AppelerAsync("aria2.removeDownloadResult", gid);
    }

    /// <summary>Arrête aria2. Ce qui est déjà reçu reste dans les dossiers, rajouter le même torrent le reprendra.</summary>
    public void Arreter()
    {
        if (_aria2 is not { } processus) return;
        _aria2 = null;
        try
        {
            if (!processus.HasExited)
            {
                // Demande polie d'abord, pour qu'il note où il en est, puis arrêt ferme s'il tarde.
                try { AppelerAsync("aria2.shutdown").Wait(1500); }
                catch (Exception e) when (e is AggregateException or InvalidOperationException) { }
                if (!processus.WaitForExit(2500)) processus.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Il s'est arrêté entre-temps, c'est ce qu'on voulait.
        }
        finally
        {
            processus.Dispose();
        }
    }

    private static string Texte(JsonElement objet, string champ) =>
        objet.TryGetProperty(champ, out var valeur) && valeur.ValueKind == JsonValueKind.String ? valeur.GetString() ?? "" : "";

    // aria2 écrit tous ses nombres sous forme de texte.
    private static double Nombre(JsonElement objet, string champ) =>
        double.TryParse(Texte(objet, champ), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static readonly Dictionary<string, string> Logiciels = new(StringComparer.Ordinal)
    {
        ["qB"] = "qBittorrent", ["TR"] = "Transmission", ["UT"] = "µTorrent", ["UM"] = "µTorrent Mac", ["DE"] = "Deluge",
        ["lt"] = "rTorrent", ["LT"] = "libtorrent", ["A2"] = "aria2", ["BC"] = "BitComet", ["AZ"] = "Vuze", ["BI"] = "BiglyBT",
        ["WW"] = "WebTorrent", ["BT"] = "BitTorrent", ["FD"] = "Free Download Manager", ["TX"] = "Tixati", ["KT"] = "KTorrent",
        ["RT"] = "rTorrent", ["SD"] = "Xunlei", ["XL"] = "Xunlei", ["FL"] = "Flud", ["tT"] = "tTorrent",
    };

    /// <summary>
    /// Le logiciel d'un pair, lu dans son identifiant. La forme la plus répandue est « -qB4650- » : deux lettres
    /// pour le logiciel, quatre caractères pour sa version. L'identifiant arrive avec ses octets écrits « %XX ».
    /// </summary>
    private static string Logiciel(string identifiant)
    {
        var clair = new StringBuilder();
        for (var i = 0; i < identifiant.Length && clair.Length < 8; i++)
        {
            if (identifiant[i] == '%' && i + 2 < identifiant.Length
                && int.TryParse(identifiant.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var octet))
            {
                clair.Append((char)octet);
                i += 2;
            }
            else clair.Append(identifiant[i]);
        }
        var id = clair.ToString();
        if (id.Length < 8 || id[0] != '-' || id[7] != '-') return "inconnu";
        var nom = Logiciels.GetValueOrDefault(id[1..3], id[1..3]);
        var version = id[3..7];
        // Chaque caractère de la version est un nombre : 0 à 9, puis A pour 10, B pour 11… (« 0D8 » se lit 0.13.8).
        static int Rang(char c) => char.IsDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
        return version.All(char.IsAsciiLetterOrDigit) ? $"{nom} {Rang(version[0])}.{Rang(version[1])}.{Rang(version[2])}" : nom;
    }
}
