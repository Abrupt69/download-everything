using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtDlpInterface;

/// <summary>
/// Lit la page publique d'un lien Spotify, celle que Spotify sert pour afficher un lecteur dans un autre site.
/// Elle contient la liste des morceaux (titre, artiste, durée) et répond en moins d'une seconde, là où spotDL
/// met plusieurs minutes pour un album. Aucun compte n'est nécessaire et aucun son n'est pris à Spotify.
/// C'est la première source essayée. Si sa forme change un jour, spotDL prend le relais.
/// </summary>
internal static partial class PageSpotify
{
    // « /track/… », « /album/… » ou « /playlist/… », parfois précédé d'un code de langue (« /intl-fr/ »).
    [GeneratedRegex(@"^/(?:intl-[a-z-]+/)?(track|album|playlist)/([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Chemin();

    // Le bloc de données que la page embarque pour son propre affichage.
    [GeneratedRegex("<script id=\"__NEXT_DATA__\" type=\"application/json\">(.+?)</script>", RegexOptions.Singleline)]
    private static partial Regex Donnees();

    /// <summary>
    /// Les morceaux du lien, ou une liste vide si la page ne les donne pas (lien d'artiste, forme inattendue).
    /// « PeutEtreIncomplete » signale une playlist dont le nombre de morceaux tombe pile sur un palier rond :
    /// la page ne montre sans doute que le début, mieux vaut alors la faire relire en entier par spotDL.
    /// </summary>
    public static async Task<(List<Morceau> Morceaux, bool PeutEtreIncomplete)> LireAsync(
        HttpClient client, string adresse, CancellationToken arret)
    {
        var morceaux = new List<Morceau>();
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri)) return (morceaux, false);
        var chemin = Chemin().Match(uri.AbsolutePath);
        if (!chemin.Success) return (morceaux, false);
        var type = chemin.Groups[1].Value.ToLowerInvariant();

        // L'adresse est reconstruite à partir du type et de l'identifiant seuls, rien d'autre du lien collé n'y entre.
        var page = await client.GetStringAsync($"https://open.spotify.com/embed/{type}/{chemin.Groups[2].Value}", arret);
        var bloc = Donnees().Match(page);
        if (!bloc.Success) return (morceaux, false);

        using var document = JsonDocument.Parse(bloc.Groups[1].Value);
        var entite = document.RootElement;
        foreach (var etage in new[] { "props", "pageProps", "state", "data", "entity" })
            if (entite.ValueKind != JsonValueKind.Object || !entite.TryGetProperty(etage, out entite)) return (morceaux, false);

        var nom = Texte(entite, "name") is { Length: > 0 } n ? n : Texte(entite, "title");
        var annee = entite.TryGetProperty("releaseDate", out var sortie) && sortie.ValueKind == JsonValueKind.Object
            ? Texte(sortie, "isoString") is { Length: >= 4 } date ? date[..4] : ""
            : "";

        if (type == "track")
        {
            var artiste = entite.TryGetProperty("artists", out var artistes) && artistes.ValueKind == JsonValueKind.Array && artistes.GetArrayLength() > 0
                ? Texte(artistes[0], "name")
                : "";
            if (nom.Length > 0) morceaux.Add(new Morceau(nom, artiste, "", "", annee, 0, Secondes(entite), "", 0));
            return (morceaux, false);
        }

        if (!entite.TryGetProperty("trackList", out var pistes) || pistes.ValueKind != JsonValueKind.Array) return (morceaux, false);
        var album = type == "album";
        var rang = 0;
        foreach (var piste in pistes.EnumerateArray())
        {
            rang++;
            var titre = Texte(piste, "title");
            if (titre.Length == 0) continue;
            // Le sous-titre énumère les artistes, séparés par des virgules. Le premier suffit à retrouver le morceau.
            var artiste = Texte(piste, "subtitle").Replace(' ', ' ').Split(',')[0].Trim();
            morceaux.Add(new Morceau(
                titre, artiste,
                Album: album ? nom : "",
                ArtisteDeLAlbum: album ? Texte(entite, "subtitle").Replace(' ', ' ') : "",
                annee,
                Piste: album ? rang : 0,
                Secondes(piste),
                Liste: nom,
                rang));
        }
        return (morceaux, type == "playlist" && morceaux.Count is 50 or 100);
    }

    private static string Texte(JsonElement element, string champ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(champ, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? (valeur.GetString() ?? "").Trim()
            : "";

    /// <summary>La durée, que la page donne en millisecondes.</summary>
    private static int Secondes(JsonElement element) =>
        element.TryGetProperty("duration", out var duree) && duree.ValueKind == JsonValueKind.Number && duree.TryGetDouble(out var ms)
            ? (int)Math.Round(ms / 1000)
            : 0;
}
