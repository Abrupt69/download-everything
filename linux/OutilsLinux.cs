using System.IO.Compression;
using System.Net.Http;

namespace YtDlpInterface;

/// <summary>
/// Les outils sous Linux. La version Windows les transporte dans son exécutable. Ici le paquet reste léger :
/// ffmpeg et aria2 viennent du système, installés avec lui et tenus à jour par lui. yt-dlp, Deno et gallery-dl,
/// qui doivent rester très récents pour suivre les sites, sont pris à leurs dépôts officiels à la première
/// ouverture, empreinte contrôlée, et déposés dans le dossier de l'utilisateur.
/// </summary>
internal static partial class MiseAJourDesOutils
{
    private const string DepotYtDlp = "yt-dlp/yt-dlp";
    private const string FichierYtDlpLinux = "yt-dlp_linux";
    private const string ArchiveDenoLinux = "deno-x86_64-unknown-linux-gnu.zip";

    /// <summary>Installe ou met à jour yt-dlp pour Linux. Rend une phrase qui dit ce qui a été fait.</summary>
    public static async Task<string> YtDlpLinuxAsync(HttpClient client, IProgress<Avancement> avancement, CancellationToken arret)
    {
        Directory.CreateDirectory(YtDlp.DossierOutils);
        using var version = await DerniereVersionAsync(client, DepotYtDlp, arret);
        var numero = version.RootElement.GetProperty("tag_name").GetString() ?? "";
        var cible = Path.Combine(YtDlp.DossierOutils, YtDlp.NomExecutable);
        if (File.Exists(cible) && LireVersions().GetValueOrDefault("yt-dlp") == numero) return "yt-dlp est déjà à jour.";

        var recu = await TelechargerAsync(client, Piece(version, FichierYtDlpLinux), DepotYtDlp, "yt-dlp", avancement, arret);
        // Une ligne « empreinte  nom » par fichier publié. Le nom est comparé en entier.
        var sommes = await client.GetStringAsync(Adresse(Piece(version, "SHA2-256SUMS"), DepotYtDlp), arret);
        var ligne = sommes.Split('\n').FirstOrDefault(l =>
            l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [_, var nom] && nom.TrimStart('*') == FichierYtDlpLinux) ?? "";
        await VerifierAsync(recu, Empreinte().Match(ligne).Value, "yt-dlp", arret);

        arret.ThrowIfCancellationRequested();
        File.Move(recu, cible, overwrite: true);
        Plateforme.RendreExecutable(cible);
        NoterVersion("yt-dlp", numero);
        return $"yt-dlp installé (version {numero}).";
    }

    /// <summary>Installe ou met à jour Deno pour Linux. Rend une phrase qui dit ce qui a été fait.</summary>
    public static async Task<string> DenoLinuxAsync(HttpClient client, IProgress<Avancement> avancement, CancellationToken arret)
    {
        Directory.CreateDirectory(YtDlp.DossierOutils);
        using var version = await DerniereVersionAsync(client, DepotDeno, arret);
        var numero = version.RootElement.GetProperty("tag_name").GetString() ?? "";
        var cible = Path.Combine(YtDlp.DossierOutils, "deno");
        if (File.Exists(cible) && LireVersions().GetValueOrDefault("deno") == numero) return "Deno est déjà à jour.";

        var zip = await TelechargerAsync(client, Piece(version, ArchiveDenoLinux), DepotDeno, "Deno", avancement, arret);
        var texte = await client.GetStringAsync(Adresse(Piece(version, ArchiveDenoLinux + ".sha256sum"), DepotDeno), arret);
        await VerifierAsync(zip, Empreinte().Match(texte.Replace("\0", "")).Value, "Deno", arret);

        var extrait = Path.Combine(DossierDeTravail, "deno");
        using (var lecture = ZipFile.OpenRead(zip))
        {
            var entree = lecture.Entries.FirstOrDefault(e => e.Name == "deno")
                         ?? throw new InvalidDataException("L'archive de Deno ne contient pas le programme deno.");
            entree.ExtractToFile(extrait, overwrite: true);
        }

        arret.ThrowIfCancellationRequested();
        File.Move(extrait, cible, overwrite: true);
        Plateforme.RendreExecutable(cible);
        NoterVersion("deno", numero);
        return $"Deno installé (version {numero.TrimStart('v')}).";
    }

    /// <summary>Les trois outils pris aux dépôts officiels, dans l'ordre où l'on en a besoin.</summary>
    public static (string Nom, Func<HttpClient, IProgress<Avancement>, CancellationToken, Task<string>> Tache)[] OutilsLinux =>
    [
        ("yt-dlp", YtDlpLinuxAsync),
        ("Deno", DenoLinuxAsync),
        ("gallery-dl", GalleryDlAsync),
    ];

    /// <summary>Vrai quand un des trois outils manque encore dans le dossier de l'utilisateur.</summary>
    public static bool OutilsLinuxIncomplets() =>
        YtDlp.Trouver() is null || YtDlp.TrouverComplements().Deno is null || GalleryDl.Trouver() is null;
}