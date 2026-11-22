using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YtDlpInterface;

/// <summary>
/// Connexion au compte Spotify de l'utilisateur, par la voie officielle de Spotify (code d'autorisation avec PKCE).
/// Depuis février 2026 c'est le seul moyen documenté de lire tous les titres d'une playlist, et seulement pour les
/// playlists dont on est propriétaire ou collaborateur. Le mot de passe est tapé sur la page de Spotify, dans le
/// navigateur. L'application ne reçoit qu'un jeton, limité à la lecture des playlists, gardé chiffré sur ce PC.
/// Aucun son n'est pris à Spotify, seule la liste des titres est lue.
/// </summary>
internal static class CompteSpotify
{
    /// <summary>L'adresse à déclarer dans l'application Spotify, case « Redirect URIs ». Spotify exige l'adresse chiffrée, pas « localhost ».</summary>
    public const string AdresseDeRetour = "http://127.0.0.1:8765/callback";
    public const string TableauDeBord = "https://developer.spotify.com/dashboard";

    private const int Port = 8765;
    private const string Droits = "playlist-read-private playlist-read-collaborative";

    // Jeton d'accès du moment, valable une heure. Il n'est jamais écrit sur le disque.
    private static string? _acces;
    private static DateTime _finDeLAcces;

    public static string IdClient => Reglages.Lire("spotify-client-id", "");

    public static bool EstConnecte => IdClient.Length > 0 && Reglages.Lire("spotify-jeton", "").Length > 0;

    /// <summary>Un identifiant d'application Spotify est fait de 32 chiffres et lettres de a à f.</summary>
    public static bool IdValide(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    public static void Deconnecter()
    {
        _acces = null;
        Reglages.Ecrire("spotify-jeton", "");
    }

    /// <summary>
    /// Ouvre la page de connexion de Spotify dans le navigateur, attend le retour sur ce PC, puis échange le code
    /// reçu contre un jeton. Lève une exception au message lisible si la connexion n'aboutit pas.
    /// </summary>
    public static async Task ConnecterAsync(string idClient, HttpClient client, CancellationToken arret)
    {
        // PKCE. Un secret tiré au hasard pour cette seule connexion, dont Spotify ne reçoit d'abord que l'empreinte.
        // Il remplace le « client secret », qu'une application installée sur un PC ne pourrait pas garder secret.
        var verificateur = Base64Url(RandomNumberGenerator.GetBytes(64));
        var defi = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificateur)));
        var etat = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var ecoute = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            ecoute.Start();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException($"Le port {Port} de ce PC est déjà pris par un autre programme. Fermez-le, puis réessayez.");
        }

        var page = "https://accounts.spotify.com/authorize"
                   + $"?client_id={idClient}&response_type=code&redirect_uri={Uri.EscapeDataString(AdresseDeRetour)}"
                   + $"&code_challenge_method=S256&code_challenge={defi}&state={etat}&scope={Uri.EscapeDataString(Droits)}";
        Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();

        var code = await AttendreLeRetourAsync(ecoute, etat, arret);

        using var demande = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = AdresseDeRetour,
            ["client_id"] = idClient,
            ["code_verifier"] = verificateur,
        });
        using var reponse = await client.PostAsync("https://accounts.spotify.com/api/token", demande, arret);
        var texte = await reponse.Content.ReadAsStringAsync(arret);
        if (!reponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify a refusé la connexion (code {(int)reponse.StatusCode}). {Motif(texte)}");

        Reglages.Ecrire("spotify-client-id", idClient);
        RetenirLesJetons(texte);
    }

    /// <summary>
    /// Attend que le navigateur revienne sur ce PC avec le code d'autorisation, et lui répond par une page courte.
    /// Seule la première ligne de la demande est lue, elle contient tout ce qu'il faut.
    /// </summary>
    private static async Task<string> AttendreLeRetourAsync(TcpListener ecoute, string etat, CancellationToken arret)
    {
        while (true)
        {
            using var liaison = await ecoute.AcceptTcpClientAsync(arret);
            await using var flux = liaison.GetStream();
            using var lecture = new StreamReader(flux, Encoding.ASCII, false, 4096, leaveOpen: true);
            var ligne = await lecture.ReadLineAsync(arret) ?? "";

            // « GET /callback?code=…&state=… HTTP/1.1 »
            var morceaux = ligne.Split(' ');
            var chemin = morceaux.Length > 1 ? morceaux[1] : "";
            var parametres = chemin.Contains('?')
                ? chemin[(chemin.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal)
                : [];

            // Le navigateur demande aussi parfois une icône. Toute demande qui n'est pas le retour attendu est ignorée.
            var estLeRetour = chemin.StartsWith("/callback", StringComparison.Ordinal);
            var reussi = estLeRetour && parametres.GetValueOrDefault("state") == etat && parametres.ContainsKey("code");
            var message = !estLeRetour ? "" : reussi
                ? "Connexion réussie. Vous pouvez fermer cet onglet et revenir à Download Everything."
                : "La connexion n'a pas abouti. Revenez à Download Everything et réessayez.";
            var corps = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Download Everything</title><body style=\"font-family:Segoe UI,sans-serif;background:#141414;color:#ededed;padding:48px\"><p>{message}</p>");
            var entete = Encoding.ASCII.GetBytes($"HTTP/1.1 {(estLeRetour ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {corps.Length}\r\nConnection: close\r\n\r\n");
            await flux.WriteAsync(entete, arret);
            await flux.WriteAsync(corps, arret);

            if (!estLeRetour) continue;
            if (reussi) return parametres["code"];
            throw new InvalidOperationException(parametres.TryGetValue("error", out var erreur)
                ? $"Spotify n'a pas donné l'autorisation ({erreur})."
                : "Le retour de Spotify n'était pas celui attendu.");
        }
    }

    /// <summary>Un jeton d'accès valable, renouvelé au besoin. Null si l'on n'est pas connecté ou si Spotify refuse.</summary>
    private static async Task<string?> JetonAsync(HttpClient client, CancellationToken arret)
    {
        if (_acces is not null && DateTime.UtcNow < _finDeLAcces) return _acces;
        var renouvellement = Dechiffrer(Reglages.Lire("spotify-jeton", ""));
        if (renouvellement is null || IdClient.Length == 0) return null;

        using var demande = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = renouvellement,
            ["client_id"] = IdClient,
        });
        using var reponse = await client.PostAsync("https://accounts.spotify.com/api/token", demande, arret);
        if (!reponse.IsSuccessStatusCode) return null;
        RetenirLesJetons(await reponse.Content.ReadAsStringAsync(arret));
        return _acces;
    }

    private static void RetenirLesJetons(string json)
    {
        using var document = JsonDocument.Parse(json);
        var racine = document.RootElement;
        _acces = racine.GetProperty("access_token").GetString();
        var duree = racine.TryGetProperty("expires_in", out var secondes) && secondes.TryGetInt32(out var s) ? s : 3600;
        _finDeLAcces = DateTime.UtcNow.AddSeconds(duree - 60);
        // Spotify peut rendre un nouveau jeton de renouvellement, qui remplace alors l'ancien.
        if (racine.TryGetProperty("refresh_token", out var nouveau) && nouveau.GetString() is { Length: > 0 } jeton)
            Reglages.Ecrire("spotify-jeton", Chiffrer(jeton));
    }

    /// <summary>
    /// Lit tous les titres d'une playlist, par pages de 50. Rend le nom de la playlist et ses morceaux.
    /// Lève une exception au message lisible quand Spotify refuse, par exemple pour une playlist dont on n'est
    /// ni propriétaire ni collaborateur.
    /// </summary>
    public static async Task<(string Nom, List<Morceau> Morceaux)> LirePlaylistAsync(
        HttpClient client, string idPlaylist, IProgress<(int Lus, int Total)>? avancement, CancellationToken arret)
    {
        var jeton = await JetonAsync(client, arret)
                    ?? throw new InvalidOperationException("La connexion à Spotify a expiré, reconnectez-vous dans la zone Compte Spotify.");

        using var fiche = await DemanderAsync(client, jeton, $"https://api.spotify.com/v1/playlists/{idPlaylist}?fields=name", arret);
        var nom = fiche.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        var morceaux = new List<Morceau>();
        var total = 0;
        string? suivante = $"https://api.spotify.com/v1/playlists/{idPlaylist}/items?limit=50&offset=0";
        while (suivante is not null)
        {
            using var page = await DemanderAsync(client, jeton, suivante, arret);
            var racine = page.RootElement;
            if (racine.TryGetProperty("total", out var t) && t.TryGetInt32(out var nombre)) total = nombre;
            if (!racine.TryGetProperty("items", out var entrees) || entrees.ValueKind != JsonValueKind.Array) break;

            foreach (var entree in entrees.EnumerateArray())
            {
                // Le titre est sous « item » depuis 2026. On lit aussi l'ancien nom, « track », au cas où.
                if (!(entree.TryGetProperty("item", out var titre) || entree.TryGetProperty("track", out titre))
                    || titre.ValueKind != JsonValueKind.Object) continue;
                var nomDuTitre = Texte(titre, "name");
                // Un épisode de podcast ou un fichier local n'a pas d'équivalent à chercher ailleurs.
                if (nomDuTitre.Length == 0 || Texte(titre, "type") is not ("track" or "")) continue;

                var album = titre.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                var sortie = album.ValueKind == JsonValueKind.Object ? Texte(album, "release_date") : "";
                morceaux.Add(new Morceau(
                    nomDuTitre,
                    PremierArtiste(titre),
                    album.ValueKind == JsonValueKind.Object ? Texte(album, "name") : "",
                    album.ValueKind == JsonValueKind.Object ? PremierArtiste(album) : "",
                    sortie.Length >= 4 ? sortie[..4] : "",
                    Piste: titre.TryGetProperty("track_number", out var piste) && piste.TryGetInt32(out var p) ? p : 0,
                    Duree: titre.TryGetProperty("duration_ms", out var duree) && duree.TryGetDouble(out var ms) ? (int)Math.Round(ms / 1000) : 0,
                    Liste: nom,
                    Rang: morceaux.Count + 1));
            }
            avancement?.Report((morceaux.Count, total));

            // Spotify donne lui-même l'adresse de la page suivante. On ne la suit que si elle reste chez lui.
            suivante = racine.TryGetProperty("next", out var lien) && lien.GetString() is { Length: > 0 } adresse
                       && adresse.StartsWith("https://api.spotify.com/", StringComparison.Ordinal) ? adresse : null;
        }

        // Trois chiffres pour le rang d'une grande playlist, pour que les fichiers se rangent dans l'ordre.
        var largeur = morceaux.Count >= 100 ? 3 : 2;
        return (nom, morceaux.Select(m => m with { LargeurDuRang = largeur }).ToList());
    }

    private static async Task<JsonDocument> DemanderAsync(HttpClient client, string jeton, string adresse, CancellationToken arret)
    {
        using var demande = new HttpRequestMessage(HttpMethod.Get, adresse);
        demande.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
        using var reponse = await client.SendAsync(demande, arret);
        var texte = await reponse.Content.ReadAsStringAsync(arret);
        if (reponse.IsSuccessStatusCode) return JsonDocument.Parse(texte);
        // Le motif écrit par Spotify est joint tel quel. Un refus peut avoir plusieurs causes (playlist d'un autre,
        // compte absent de la liste des utilisateurs de l'application, abonnement), et lui seul dit laquelle.
        var motif = Motif(texte);
        var quoi = adresse.Contains("/items", StringComparison.Ordinal) ? "la liste des titres" : "la fiche de la playlist";
        throw new InvalidOperationException(reponse.StatusCode switch
        {
            HttpStatusCode.Forbidden => $"Spotify a refusé {quoi} (code 403{(motif.Length > 0 ? $", « {motif} »" : "")}). "
                                        + "Il ne laisse lire en entier que vos propres playlists et celles où vous êtes collaborateur.",
            HttpStatusCode.Unauthorized => "La connexion à Spotify a expiré, reconnectez-vous dans la zone Compte Spotify.",
            HttpStatusCode.NotFound => "Spotify ne trouve pas cette playlist.",
            HttpStatusCode.TooManyRequests => "Spotify demande de patienter avant de nouvelles lectures.",
            _ => $"Spotify a répondu par une erreur (code {(int)reponse.StatusCode}). {Motif(texte)}",
        });
    }

    private static string PremierArtiste(JsonElement element) =>
        element.TryGetProperty("artists", out var artistes) && artistes.ValueKind == JsonValueKind.Array && artistes.GetArrayLength() > 0
            ? Texte(artistes[0], "name")
            : "";

    private static string Texte(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? (valeur.GetString() ?? "").Trim()
            : "";

    /// <summary>Le motif d'un refus, tel que Spotify l'écrit dans sa réponse, quand il y en a un.</summary>
    private static string Motif(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var racine = document.RootElement;
            if (racine.TryGetProperty("error_description", out var description)) return description.GetString() ?? "";
            if (racine.TryGetProperty("error", out var erreur))
                return erreur.ValueKind == JsonValueKind.Object ? Texte(erreur, "message") : erreur.GetString() ?? "";
        }
        catch (JsonException)
        {
            // Réponse qui n'est pas du JSON, il n'y a pas de motif à en tirer.
        }
        return "";
    }

    private static string Base64Url(byte[] octets) =>
        Convert.ToBase64String(octets).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Le jeton de renouvellement est chiffré par Windows pour le compte de l'utilisateur.
    // Recopié sur un autre PC ou lu depuis une autre session, le fichier des réglages ne permet pas de s'en servir.
    private static string Chiffrer(string texte) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(texte), null, DataProtectionScope.CurrentUser));

    private static string? Dechiffrer(string chiffre)
    {
        if (chiffre.Length == 0) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(chiffre), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
