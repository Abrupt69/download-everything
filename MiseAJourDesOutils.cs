using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtDlpInterface;

/// <summary>
/// Met à jour ffmpeg, Deno et spotDL depuis leurs dépôts officiels sur GitHub. yt-dlp, lui, sait se mettre à jour seul.
/// Rien n'est remplacé tant que l'archive reçue n'a pas l'empreinte SHA-256 que le dépôt publie pour elle.
/// </summary>
internal static partial class MiseAJourDesOutils
{
    /// <summary>Où en est un téléchargement, pour l'afficheur de la fenêtre.</summary>
    public sealed record Avancement(string Message, long Recu, long Total);

    // Les seules adresses d'où un fichier peut venir. Une adresse d'archive qui ne commence pas ainsi est refusée.
    private const string DepotFfmpeg = "yt-dlp/FFmpeg-Builds";
    private const string DepotDeno = "denoland/deno";
    private const string DepotSpotDl = "spotDL/spotify-downloader";
    private const string ArchiveFfmpeg = "ffmpeg-master-latest-win64-gpl-shared.zip";
    private const string ArchiveDeno = "deno-x86_64-pc-windows-msvc.zip";

    private static string FichierVersions => Path.Combine(YtDlp.DossierOutils, "versions.txt");
    private static string DossierDeTravail => Path.Combine(YtDlp.DossierOutils, "mise-a-jour");

    // Bibliothèques de ffmpeg, dont le numéro change d'une version à l'autre (avcodec-63.dll, swscale-10.dll…).
    [GeneratedRegex(@"^(av[a-z]+|sw[a-z]+|postproc)-\d+\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex BibliothequeFfmpeg();

    [GeneratedRegex("[0-9a-fA-F]{64}")]
    private static partial Regex Empreinte();

    public static HttpClient NouveauClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub refuse les demandes sans nom de client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadEverything/1.0");
        return client;
    }

    /// <summary>Versions notées lors du dernier dépôt ou de la dernière mise à jour, une ligne « outil=version » par outil.</summary>
    public static Dictionary<string, string> LireVersions()
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FichierVersions)) return versions;
            foreach (var ligne in File.ReadAllLines(FichierVersions))
            {
                var coupe = ligne.IndexOf('=');
                if (coupe > 0) versions[ligne[..coupe].Trim()] = ligne[(coupe + 1)..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sans ce fichier on considère simplement que tout est à vérifier.
        }
        return versions;
    }

    private static void NoterVersion(string outil, string version)
    {
        var versions = LireVersions();
        versions[outil] = version;
        File.WriteAllLines(FichierVersions, versions.Select(v => $"{v.Key}={v.Value}"));
    }

    /// <summary>Met à jour ffmpeg et ffprobe. Rend une phrase qui dit ce qui a été fait.</summary>
    public static async Task<string> FfmpegAsync(HttpClient client, IProgress<Avancement> avancement, CancellationToken arret)
    {
        using var version = await DerniereVersionAsync(client, DepotFfmpeg, arret);
        var archive = Piece(version, ArchiveFfmpeg);
        // Ce dépôt republie la même étiquette à chaque construction, c'est donc la date de l'archive qui fait foi.
        var date = archive.GetProperty("updated_at").GetString() ?? "";
        if (LireVersions().GetValueOrDefault("ffmpeg") == date && File.Exists(Path.Combine(YtDlp.DossierOutils, "ffmpeg.exe")))
            return "ffmpeg est déjà à jour.";

        var zip = await TelechargerAsync(client, archive, DepotFfmpeg, "ffmpeg", avancement, arret);
        var sommes = await client.GetStringAsync(Adresse(Piece(version, "checksums.sha256"), DepotFfmpeg), arret);
        var ligne = sommes.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith(ArchiveFfmpeg, StringComparison.Ordinal)) ?? "";
        await VerifierAsync(zip, Empreinte().Match(ligne).Value, "ffmpeg", arret);

        // Extraction à part, puis remplacement d'un coup. Seul le nom du fichier est gardé,
        // un chemin écrit dans l'archive ne peut donc rien déposer hors du dossier des outils.
        var extraits = Path.Combine(DossierDeTravail, "ffmpeg");
        Directory.CreateDirectory(extraits);
        var noms = new List<string>();
        using (var lecture = ZipFile.OpenRead(zip))
        {
            foreach (var entree in lecture.Entries)
            {
                var programme = entree.FullName.Contains("/bin/", StringComparison.Ordinal)
                                && (entree.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entree.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                && !entree.Name.Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase);
                var licence = entree.Name == "LICENSE.txt";
                if (!programme && !licence) continue;
                var nom = licence ? "LICENCE-ffmpeg.txt" : entree.Name;
                entree.ExtractToFile(Path.Combine(extraits, nom), overwrite: true);
                noms.Add(nom);
            }
        }
        if (!noms.Contains("ffmpeg.exe")) throw new InvalidDataException("L'archive de ffmpeg ne contient pas ffmpeg.exe.");

        arret.ThrowIfCancellationRequested();   // dernier moment où l'on peut encore renoncer sans rien laisser à moitié fait
        foreach (var ancien in Directory.EnumerateFiles(YtDlp.DossierOutils, "*.dll"))
        {
            var nom = Path.GetFileName(ancien);
            if (BibliothequeFfmpeg().IsMatch(nom) && !noms.Contains(nom, StringComparer.OrdinalIgnoreCase)) File.Delete(ancien);
        }
        foreach (var nom in noms)
            File.Move(Path.Combine(extraits, nom), Path.Combine(YtDlp.DossierOutils, nom), overwrite: true);

        NoterVersion("ffmpeg", date);
        return $"ffmpeg mis à jour (version du {DateLisible(date)}).";
    }

    /// <summary>Met à jour Deno. Rend une phrase qui dit ce qui a été fait.</summary>
    public static async Task<string> DenoAsync(HttpClient client, IProgress<Avancement> avancement, CancellationToken arret)
    {
        using var version = await DerniereVersionAsync(client, DepotDeno, arret);
        var numero = version.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (LireVersions().GetValueOrDefault("deno") == numero && File.Exists(Path.Combine(YtDlp.DossierOutils, "deno.exe")))
            return "Deno est déjà à jour.";

        var zip = await TelechargerAsync(client, Piece(version, ArchiveDeno), DepotDeno, "Deno", avancement, arret);
        // Ce fichier d'empreinte est écrit par un outil Windows, parfois en UTF-16. On retire les octets nuls avant de le lire.
        var octets = await client.GetByteArrayAsync(Adresse(Piece(version, ArchiveDeno + ".sha256sum"), DepotDeno), arret);
        var texte = Encoding.UTF8.GetString(octets).Replace("\0", "");
        await VerifierAsync(zip, Empreinte().Match(texte).Value, "Deno", arret);

        var extrait = Path.Combine(DossierDeTravail, "deno.exe");
        using (var lecture = ZipFile.OpenRead(zip))
        {
            var entree = lecture.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException("L'archive de Deno ne contient pas deno.exe.");
            entree.ExtractToFile(extrait, overwrite: true);
        }

        arret.ThrowIfCancellationRequested();
        File.Move(extrait, Path.Combine(YtDlp.DossierOutils, "deno.exe"), overwrite: true);
        NoterVersion("deno", numero);
        return $"Deno mis à jour (version {numero.TrimStart('v')}).";
    }

    /// <summary>Met à jour spotDL. Rend une phrase qui dit ce qui a été fait.</summary>
    public static async Task<string> SpotDlAsync(HttpClient client, IProgress<Avancement> avancement, CancellationToken arret)
    {
        using var version = await DerniereVersionAsync(client, DepotSpotDl, arret);
        var numero = version.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (LireVersions().GetValueOrDefault("spotdl") == numero && File.Exists(Path.Combine(YtDlp.DossierOutils, "spotdl.exe")))
            return "spotDL est déjà à jour.";

        // Le nom du fichier porte le numéro de version, on le reconnaît à son début et à sa fin.
        JsonElement? executable = null;
        foreach (var piece in version.RootElement.GetProperty("assets").EnumerateArray())
        {
            var nom = piece.GetProperty("name").GetString() ?? "";
            if (nom.StartsWith("spotdl-", StringComparison.OrdinalIgnoreCase) && nom.EndsWith("-win32.exe", StringComparison.OrdinalIgnoreCase))
                executable = piece;
        }
        if (executable is not { } trouve) throw new InvalidDataException("L'exécutable Windows de spotDL est absent de la dernière version publiée.");

        // Ce dépôt ne publie pas de fichier d'empreintes. On prend celle que GitHub calcule et affiche pour chaque fichier.
        var empreinte = trouve.TryGetProperty("digest", out var digest) ? Empreinte().Match(digest.GetString() ?? "").Value : "";
        var recu = await TelechargerAsync(client, trouve, DepotSpotDl, "spotDL", avancement, arret);
        await VerifierAsync(recu, empreinte, "spotDL", arret);

        arret.ThrowIfCancellationRequested();
        File.Move(recu, Path.Combine(YtDlp.DossierOutils, "spotdl.exe"), overwrite: true);
        NoterVersion("spotdl", numero);
        return $"spotDL mis à jour (version {numero.TrimStart('v')}).";
    }

    public static void Nettoyer()
    {
        try
        {
            if (Directory.Exists(DossierDeTravail)) Directory.Delete(DossierDeTravail, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Des restes sans conséquence, ils seront réécrits à la prochaine mise à jour.
        }
    }

    /// <summary>« 2026-11-16T23:38:18Z » devient « 16/11/2026 ».</summary>
    public static string DateLisible(string date) =>
        DateTime.TryParse(date, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d.ToString("dd/MM/yyyy") : date;

    private static async Task<JsonDocument> DerniereVersionAsync(HttpClient client, string depot, CancellationToken arret)
    {
        using var reponse = await client.GetAsync($"https://api.github.com/repos/{depot}/releases/latest", arret);
        reponse.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(arret));
    }

    private static JsonElement Piece(JsonDocument version, string nom)
    {
        foreach (var piece in version.RootElement.GetProperty("assets").EnumerateArray())
            if (piece.GetProperty("name").GetString() == nom) return piece;
        throw new InvalidDataException($"Le fichier {nom} est absent de la dernière version publiée.");
    }

    private static string Adresse(JsonElement piece, string depot)
    {
        var adresse = piece.GetProperty("browser_download_url").GetString() ?? "";
        if (!adresse.StartsWith($"https://github.com/{depot}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException($"Adresse de téléchargement inattendue, refusée. {adresse}");
        return adresse;
    }

    private static async Task<string> TelechargerAsync(
        HttpClient client, JsonElement piece, string depot, string quoi, IProgress<Avancement> avancement, CancellationToken arret)
    {
        Directory.CreateDirectory(DossierDeTravail);
        var cible = Path.Combine(DossierDeTravail, quoi.ToLowerInvariant() + ".zip");
        using var reponse = await client.GetAsync(Adresse(piece, depot), HttpCompletionOption.ResponseHeadersRead, arret);
        reponse.EnsureSuccessStatusCode();
        var total = reponse.Content.Headers.ContentLength ?? piece.GetProperty("size").GetInt64();

        await using var source = await reponse.Content.ReadAsStreamAsync(arret);
        await using var sortie = new FileStream(cible, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var tampon = new byte[1 << 16];
        long recu = 0;
        var dernierRapport = Environment.TickCount64;
        int lu;
        while ((lu = await source.ReadAsync(tampon, arret)) > 0)
        {
            await sortie.WriteAsync(tampon.AsMemory(0, lu), arret);
            recu += lu;
            // Quatre rapports par seconde suffisent à l'œil et ne chargent pas la fenêtre.
            if (Environment.TickCount64 - dernierRapport < 250) continue;
            dernierRapport = Environment.TickCount64;
            avancement.Report(new Avancement($"Téléchargement de {quoi}…", recu, total));
        }
        avancement.Report(new Avancement($"Vérification de {quoi}…", recu, total));
        return cible;
    }

    private static async Task VerifierAsync(string fichier, string attendue, string quoi, CancellationToken arret)
    {
        if (attendue.Length != 64) throw new InvalidDataException($"L'empreinte publiée pour {quoi} est illisible, rien n'a été remplacé.");
        await using var lecture = File.OpenRead(fichier);
        var reelle = Convert.ToHexString(await SHA256.HashDataAsync(lecture, arret));
        if (!reelle.Equals(attendue, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"L'archive de {quoi} n'a pas l'empreinte publiée par son dépôt, rien n'a été remplacé.");
    }
}
