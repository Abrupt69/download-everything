using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtDlpInterface;

/// <summary>
/// Liens Deezer. Le catalogue public de Deezer se lit sans compte ni clé, par pages de cent titres,
/// et sans plafond : une playlist de 832 titres a été lue en entier aux essais. Seule la liste des titres
/// est lue, aucun son n'est pris à Deezer.
/// </summary>
internal static partial class CatalogueDeezer
{
    // « /track/123 », « /album/123 », « /playlist/123 », parfois précédé d'un code de langue (« /fr/ »).
    [GeneratedRegex(@"/(?:[a-z]{2}/)?(track|album|playlist)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Chemin();

    public static bool EstUneAdresse(string adresse) =>
        Uri.TryCreate(adresse, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("deezer.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("deezer.page.link", StringComparison.OrdinalIgnoreCase));

    public static async Task<List<Morceau>> LireAsync(
        HttpClient client, string adresse, IProgress<(int Lus, int Total)>? avancement, CancellationToken arret)
    {
        var chemin = Chemin().Match(new Uri(adresse).AbsolutePath);
        if (!chemin.Success)
        {
            // Lien de partage court. On suit sa redirection, et on ne garde l'adresse finale que si elle est bien chez Deezer.
            using var reponse = await client.GetAsync(adresse, HttpCompletionOption.ResponseHeadersRead, arret);
            var finale = reponse.RequestMessage?.RequestUri;
            if (finale is null || !finale.Host.EndsWith("deezer.com", StringComparison.OrdinalIgnoreCase)) return [];
            chemin = Chemin().Match(finale.AbsolutePath);
            if (!chemin.Success) return [];
        }
        var type = chemin.Groups[1].Value.ToLowerInvariant();
        var id = chemin.Groups[2].Value;
        var morceaux = new List<Morceau>();

        if (type == "track")
        {
            using var fiche = await DemanderAsync(client, $"https://api.deezer.com/track/{id}", arret);
            var t = fiche.RootElement;
            morceaux.Add(new Morceau(
                Texte(t, "title"), Texte(Sous(t, "artist"), "name"), Texte(Sous(t, "album"), "title"), "",
                Annee(Texte(t, "release_date")), Entier(t, "track_position"), Entier(t, "duration"), "", 0));
            return morceaux;
        }

        // Album ou playlist. D'abord sa fiche, pour son nom et son nombre de titres, puis ses titres page par page.
        using var entete = await DemanderAsync(client, $"https://api.deezer.com/{type}/{id}?limit=1", arret);
        var liste = Texte(entete.RootElement, "title");
        var total = Entier(entete.RootElement, "nb_tracks");
        var album = type == "album";
        var artisteDeLAlbum = album ? Texte(Sous(entete.RootElement, "artist"), "name") : "";
        var annee = album ? Annee(Texte(entete.RootElement, "release_date")) : "";

        string? suivante = $"https://api.deezer.com/{type}/{id}/tracks?index=0&limit=100";
        while (suivante is not null)
        {
            using var page = await DemanderAsync(client, suivante, arret);
            if (!page.RootElement.TryGetProperty("data", out var titres) || titres.ValueKind != JsonValueKind.Array) break;
            foreach (var t in titres.EnumerateArray())
            {
                var titre = Texte(t, "title");
                if (titre.Length == 0) continue;
                morceaux.Add(new Morceau(
                    titre,
                    Texte(Sous(t, "artist"), "name"),
                    album ? liste : Texte(Sous(t, "album"), "title"),
                    artisteDeLAlbum,
                    annee,
                    Piste: album ? Entier(t, "track_position") : 0,
                    Duree: Entier(t, "duration"),
                    Liste: liste,
                    Rang: morceaux.Count + 1));
            }
            avancement?.Report((morceaux.Count, total));
            // Deezer donne lui-même l'adresse de la page suivante. On ne la suit que si elle reste chez lui.
            suivante = page.RootElement.TryGetProperty("next", out var lien) && lien.GetString() is { Length: > 0 } adresseSuivante
                       && adresseSuivante.StartsWith("https://api.deezer.com/", StringComparison.Ordinal) ? adresseSuivante : null;
        }

        var largeur = morceaux.Count >= 100 ? 3 : 2;
        return morceaux.Select(m => m with { LargeurDuRang = largeur }).ToList();
    }

    private static async Task<JsonDocument> DemanderAsync(HttpClient client, string adresse, CancellationToken arret)
    {
        var document = JsonDocument.Parse(await client.GetStringAsync(adresse, arret));
        // Deezer répond « 200 » même en cas d'erreur, et la décrit dans le corps de la réponse.
        if (document.RootElement.TryGetProperty("error", out var erreur))
        {
            var message = Texte(erreur, "message");
            document.Dispose();
            throw new InvalidOperationException($"Deezer a refusé la lecture. {message}");
        }
        return document;
    }

    private static JsonElement Sous(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) ? valeur : default;

    private static string Texte(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? (valeur.GetString() ?? "").Trim()
            : "";

    private static int Entier(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) && valeur.TryGetInt32(out var nombre) ? nombre : 0;

    private static string Annee(string date) => date.Length >= 4 ? date[..4] : "";
}

/// <summary>
/// Liens Apple Music. Les albums et les morceaux se lisent dans le catalogue public d'Apple, sans compte.
/// Une playlist donne ses titres dans sa page publique, et le catalogue complète chaque titre avec son artiste
/// et son album. Seule la liste des titres est lue, aucun son n'est pris à Apple.
/// </summary>
internal static partial class CatalogueApple
{
    // « /fr/album/nom/123 », « /us/playlist/nom/pl.abc », « /us/song/nom/123 ».
    [GeneratedRegex(@"^/([a-z]{2})/(album|playlist|song)/(?:[^/]+/)?((?:pl\.)?[0-9A-Za-z\.\-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Chemin();

    // Le bloc de données que la page embarque pour les moteurs de recherche.
    [GeneratedRegex(@"<script[^>]+application/ld\+json[^>]*>(.+?)</script>", RegexOptions.Singleline)]
    private static partial Regex Donnees();

    // L'identifiant d'un morceau, en fin de son adresse.
    [GeneratedRegex(@"/song/(?:[^/]+/)?(\d+)")]
    private static partial Regex IdDeMorceau();

    public static bool EstUneAdresse(string adresse) =>
        Uri.TryCreate(adresse, UriKind.Absolute, out var uri) && uri.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<Morceau>> LireAsync(
        HttpClient client, string adresse, IProgress<(int Lus, int Total)>? avancement, CancellationToken arret)
    {
        var uri = new Uri(adresse);
        var chemin = Chemin().Match(uri.AbsolutePath);
        if (!chemin.Success) return [];
        var pays = chemin.Groups[1].Value.ToLowerInvariant();
        var type = chemin.Groups[2].Value.ToLowerInvariant();
        var id = chemin.Groups[3].Value;

        // Un lien d'album avec « ?i=123 » désigne un seul morceau de cet album.
        var unSeul = type == "album" && System.Web.HttpUtility.ParseQueryString(uri.Query)["i"] is { Length: > 0 } i && i.All(char.IsDigit) ? i : null;
        if (type == "song" || unSeul is not null)
        {
            var fiches = await ChercherAsync(client, unSeul ?? id, pays, entites: null, arret);
            return fiches.Where(f => Texte(f, "wrapperType") == "track").Take(1).Select(f => VersMorceau(f, "", 0)).ToList();
        }

        if (type == "album")
        {
            var fiches = await ChercherAsync(client, id, pays, entites: "song", arret);
            var nom = fiches.Where(f => Texte(f, "wrapperType") == "collection").Select(f => Texte(f, "collectionName")).FirstOrDefault() ?? "";
            var pistes = fiches.Where(f => Texte(f, "wrapperType") == "track").ToList();
            return pistes.Select((f, rang) => VersMorceau(f, nom, rang + 1)).ToList();
        }

        // Playlist. La page donne l'ordre, les titres et les durées. Le catalogue donne l'artiste et l'album.
        var page = await client.GetStringAsync(adresse, arret);
        var bloc = Donnees().Match(page);
        if (!bloc.Success) return [];
        using var document = JsonDocument.Parse(bloc.Groups[1].Value);
        var racine = document.RootElement;
        var liste = Texte(racine, "name");
        if (!racine.TryGetProperty("track", out var titres) || titres.ValueKind != JsonValueKind.Array) return [];

        var entrees = titres.EnumerateArray()
            .Select(t => (Titre: Texte(t, "name"), Duree: Secondes(Texte(t, "duration")), Id: IdDeMorceau().Match(Texte(t, "url")).Groups[1].Value))
            .Where(e => e.Titre.Length > 0)
            .ToList();

        // Le catalogue accepte plusieurs identifiants à la fois. Cent par demande.
        var details = new Dictionary<string, JsonElement>();
        var conserves = new List<JsonDocument>();
        try
        {
            foreach (var lot in entrees.Where(e => e.Id.Length > 0).Select(e => e.Id).Distinct().Chunk(100))
            {
                var (document2, fiches) = await ChercherBrutAsync(client, string.Join(',', lot), pays, entites: null, arret);
                conserves.Add(document2);
                foreach (var f in fiches.Where(f => Texte(f, "wrapperType") == "track"))
                    if (f.TryGetProperty("trackId", out var trackId)) details[trackId.GetRawText()] = f;
                avancement?.Report((details.Count, entrees.Count));
            }

            var largeur = entrees.Count >= 100 ? 3 : 2;
            var morceaux = new List<Morceau>();
            foreach (var e in entrees)
            {
                // Un titre absent du catalogue de ce pays garde ce que la page en disait, sans artiste.
                var morceau = details.TryGetValue(e.Id, out var fiche)
                    ? VersMorceau(fiche, liste, morceaux.Count + 1) with { Piste = 0 }
                    : new Morceau(e.Titre, "", "", "", "", 0, e.Duree, liste, morceaux.Count + 1);
                morceaux.Add(morceau with { LargeurDuRang = largeur });
            }
            return morceaux;
        }
        finally
        {
            foreach (var d in conserves) d.Dispose();
        }
    }

    private static Morceau VersMorceau(JsonElement fiche, string liste, int rang) => new(
        Texte(fiche, "trackName"),
        Texte(fiche, "artistName"),
        Texte(fiche, "collectionName"),
        Texte(fiche, "collectionArtistName") is { Length: > 0 } a ? a : Texte(fiche, "artistName"),
        Texte(fiche, "releaseDate") is { Length: >= 4 } date ? date[..4] : "",
        Piste: fiche.TryGetProperty("trackNumber", out var piste) && piste.TryGetInt32(out var p) ? p : 0,
        Duree: fiche.TryGetProperty("trackTimeMillis", out var duree) && duree.TryGetDouble(out var ms) ? (int)Math.Round(ms / 1000) : 0,
        Liste: liste,
        Rang: rang);

    private static async Task<List<JsonElement>> ChercherAsync(HttpClient client, string ids, string pays, string? entites, CancellationToken arret)
    {
        var (document, fiches) = await ChercherBrutAsync(client, ids, pays, entites, arret);
        using (document) return fiches.Select(f => f.Clone()).ToList();
    }

    private static async Task<(JsonDocument Document, List<JsonElement> Fiches)> ChercherBrutAsync(
        HttpClient client, string ids, string pays, string? entites, CancellationToken arret)
    {
        var adresse = $"https://itunes.apple.com/lookup?id={ids}&country={pays}&limit=200" + (entites is null ? "" : $"&entity={entites}");
        var document = JsonDocument.Parse(await client.GetStringAsync(adresse, arret));
        var fiches = document.RootElement.TryGetProperty("results", out var resultats) && resultats.ValueKind == JsonValueKind.Array
            ? resultats.EnumerateArray().ToList()
            : [];
        return (document, fiches);
    }

    private static string Texte(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? (valeur.GetString() ?? "").Trim()
            : "";

    /// <summary>Une durée écrite à la norme ISO 8601, « PT3M4S », rendue en secondes.</summary>
    private static int Secondes(string duree)
    {
        try
        {
            return duree.Length > 0 ? (int)Math.Round(System.Xml.XmlConvert.ToTimeSpan(duree).TotalSeconds) : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}

/// <summary>Aiguillage des liens de musique vers le bon catalogue.</summary>
internal static class LiensDeMusique
{
    /// <summary>Le nom de la plateforme dont on sait lire les listes de titres, ou null.</summary>
    public static string? Plateforme(string adresse) =>
        SpotDl.EstUneAdresseSpotify(adresse) ? "Spotify"
        : CatalogueDeezer.EstUneAdresse(adresse) ? "Deezer"
        : CatalogueApple.EstUneAdresse(adresse) ? "Apple Music"
        : null;

    /// <summary>
    /// Le nom d'une plateforme de musique dont les listes ne se lisent pas sans compte, ou null.
    /// Leurs pages refusent les programmes ou ne contiennent aucun titre. Le dire clairement vaut mieux
    /// qu'un échec incompréhensible de yt-dlp.
    /// </summary>
    public static string? PlateformeFermee(string adresse)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return null;
        var hote = uri.Host.ToLowerInvariant();
        return hote.EndsWith("tidal.com", StringComparison.Ordinal) ? "Tidal"
            : hote.EndsWith("qobuz.com", StringComparison.Ordinal) ? "Qobuz"
            : hote.StartsWith("music.amazon.", StringComparison.Ordinal) ? "Amazon Music"
            : null;
    }
}
