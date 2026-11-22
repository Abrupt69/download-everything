using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace YtDlpInterface;

/// <summary>
/// La version de Download Everything elle-même. Le bouton « Tout mettre à jour » ne s'occupe que des outils
/// embarqués. Ici l'application regarde sa propre page de versions sur GitHub, dit quand une plus récente
/// existe, et sait se remplacer par elle.
/// </summary>
internal static class VersionDeLApplication
{
    private const string Depot = "Abrupt69/download-everything";
    private const string NomDuFichier = "DownloadEverything.exe";

    public const string PageDesVersions = $"https://github.com/{Depot}/releases/latest";

    /// <summary>Une version publiée plus récente que celle qui tourne.</summary>
    public sealed record Publication(Version Numero, string Adresse, long Taille, string Empreinte);

    public static Version Actuelle { get; } = LireLActuelle();

    private static Version LireLActuelle()
    {
        // Pour les essais seulement : faire croire à l'application qu'elle est plus ancienne, afin de dérouler
        // toute la mise à jour sans attendre qu'une version plus récente soit réellement publiée.
        if (Environment.GetEnvironmentVariable("DOWNLOAD_EVERYTHING_ESSAI_VERSION") is { Length: > 0 } essai
            && Version.TryParse(essai, out var feinte))
            return Normaliser(feinte);
        return Normaliser(typeof(VersionDeLApplication).Assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    // Trois nombres seulement. Sans cela « 1.2.0 » et « 1.2.0.0 » ne seraient pas égales.
    private static Version Normaliser(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static string Lisible(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>
    /// Rend la dernière version publiée si elle est plus récente que celle-ci, sinon null.
    /// Une seule demande, à l'adresse fixe du dépôt. Rien n'est envoyé d'autre que le nom du client.
    /// </summary>
    public static async Task<Publication?> ChercherAsync(HttpClient client, CancellationToken arret)
    {
        using var reponse = await client.GetAsync($"https://api.github.com/repos/{Depot}/releases/latest", arret);
        if (!reponse.IsSuccessStatusCode) return null;
        using var version = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(arret));

        var etiquette = version.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(etiquette.TrimStart('v', 'V'), out var lue)) return null;
        var numero = Normaliser(lue);
        if (numero <= Actuelle) return null;

        foreach (var piece in version.RootElement.GetProperty("assets").EnumerateArray())
        {
#if PORTABLE
            // Sous Linux la pièce attendue est le paquet .deb, dont le nom porte le numéro de version.
            var nomDeLaPiece = piece.GetProperty("name").GetString() ?? "";
            if (OperatingSystem.IsWindows() ? nomDeLaPiece != NomDuFichier : !nomDeLaPiece.EndsWith("_amd64.deb", StringComparison.Ordinal)) continue;
#else
            if (piece.GetProperty("name").GetString() != NomDuFichier) continue;
#endif
            var adresse = piece.GetProperty("browser_download_url").GetString() ?? "";
            // Le fichier ne peut venir que des versions de ce dépôt. Toute autre adresse est refusée.
            if (!adresse.StartsWith($"https://github.com/{Depot}/releases/download/", StringComparison.Ordinal)) return null;
            var empreinte = piece.TryGetProperty("digest", out var digest) ? (digest.GetString() ?? "") : "";
            if (empreinte.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) empreinte = empreinte[7..];
            return new Publication(numero, adresse, piece.GetProperty("size").GetInt64(), empreinte);
        }
        return null;
    }

    /// <summary>
    /// Télécharge la version publiée, contrôle son empreinte, la met à la place de l'exécutable qui tourne,
    /// la lance et arrête celle-ci. Windows interdit d'écraser un programme en marche mais permet de le renommer :
    /// l'ancien est donc mis de côté sous un autre nom, le nouveau prend sa place, et l'ancien est effacé à
    /// l'ouverture suivante. Quand tout réussit, cette méthode ne rend jamais la main : l'application s'arrête dedans.
    /// </summary>
    public static async Task InstallerEtRelancerAsync(
        HttpClient client, Publication publication, IProgress<MiseAJourDesOutils.Avancement> avancement, CancellationToken arret)
    {
        var actuel = Environment.ProcessPath ?? throw new InvalidOperationException("L'emplacement de l'application est inconnu.");
        if (publication.Empreinte.Length != 64)
            throw new InvalidDataException("L'empreinte publiée pour cette version est illisible, rien n'a été remplacé.");

        var travail = Path.Combine(YtDlp.DossierOutils, "nouvelle-version");
        Directory.CreateDirectory(travail);
        var recu = Path.Combine(travail, NomDuFichier);
        try
        {
            using (var reponse = await client.GetAsync(publication.Adresse, HttpCompletionOption.ResponseHeadersRead, arret))
            {
                reponse.EnsureSuccessStatusCode();
                var total = reponse.Content.Headers.ContentLength ?? publication.Taille;
                await using var source = await reponse.Content.ReadAsStreamAsync(arret);
                await using var sortie = new FileStream(recu, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var tampon = new byte[1 << 16];
                long octets = 0;
                var dernierRapport = Environment.TickCount64;
                int lu;
                while ((lu = await source.ReadAsync(tampon, arret)) > 0)
                {
                    await sortie.WriteAsync(tampon.AsMemory(0, lu), arret);
                    octets += lu;
                    if (Environment.TickCount64 - dernierRapport < 250) continue;
                    dernierRapport = Environment.TickCount64;
                    avancement.Report(new MiseAJourDesOutils.Avancement("Téléchargement de la nouvelle version…", octets, total));
                }
                avancement.Report(new MiseAJourDesOutils.Avancement("Vérification de la nouvelle version…", octets, total));
            }

            await using (var lecture = File.OpenRead(recu))
            {
                var reelle = Convert.ToHexString(await SHA256.HashDataAsync(lecture, arret));
                if (!reelle.Equals(publication.Empreinte, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Le fichier reçu n'a pas l'empreinte publiée pour cette version, rien n'a été remplacé.");
            }

            arret.ThrowIfCancellationRequested();   // dernier moment où l'on peut renoncer sans rien avoir touché

            // Passage délicat, mesuré le 22/11/2026. Cette application est un exécutable unique : chaque fois qu'elle a
            // besoin d'un morceau de code pas encore chargé, elle le relit dans son propre fichier, retrouvé par son
            // chemin. Dès que le nouveau fichier occupe ce chemin, elle y lirait n'importe quoi, et la première
            // action un peu nouvelle (fermer la fenêtre, par exemple) finirait en erreur. Entre le remplacement et
            // la fin il n'y a donc que trois gestes, tous déjà faits pendant la séance : déplacer deux fichiers,
            // lancer la nouvelle version, et s'arrêter net, sans passer par la fermeture ordinaire de la fenêtre.
            using var moi = Process.GetCurrentProcess();
            var ancien = actuel + Ancienne;
            if (File.Exists(ancien))
            {
                File.SetAttributes(ancien, FileAttributes.Normal);
                File.Delete(ancien);
            }
            File.Move(actuel, ancien);
            try
            {
                File.Move(recu, actuel);
                Process.Start(new ProcessStartInfo(actuel) { UseShellExecute = false })?.Dispose();
            }
            catch (Exception e)
            {
                // La nouvelle n'a pas pu prendre la place ou démarrer : elle est retirée et l'ancienne revient à son
                // chemin. C'est le même fichier qu'avant, l'application retrouve donc tout son code et reste utilisable.
                try { if (File.Exists(actuel)) File.Move(actuel, recu, overwrite: true); }
                catch (Exception retrait) when (retrait is IOException or UnauthorizedAccessException) { }
                File.Move(ancien, actuel);
                if (e is System.ComponentModel.Win32Exception)
                    throw new InvalidOperationException($"La nouvelle version n'a pas pu démarrer. {e.Message}");
                throw;
            }
            // Mise de côté discrète, pour ne pas faire apparaître un second fichier sur le bureau.
            try { File.SetAttributes(ancien, FileAttributes.Hidden); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            moi.Kill();
        }
        finally
        {
            try { if (Directory.Exists(travail)) Directory.Delete(travail, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private const string Ancienne = ".ancienne-version";

    /// <summary>
    /// Efface l'ancienne version laissée de côté par une mise à jour. Elle peut encore être tenue quelques
    /// secondes par l'application qui se ferme, on insiste donc un peu, à l'écart du fil de la fenêtre.
    /// </summary>
    public static void EffacerLAncienne()
    {
        if (Environment.ProcessPath is not { } actuel) return;
        var ancien = actuel + Ancienne;
        // Le dossier de travail de la mise à jour, que l'ancienne version n'a pas eu le temps de ranger en s'arrêtant.
        try
        {
            var travail = Path.Combine(YtDlp.DossierOutils, "nouvelle-version");
            if (Directory.Exists(travail)) Directory.Delete(travail, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!File.Exists(ancien)) return;
        _ = Task.Run(async () =>
        {
            for (var essai = 0; essai < 20; essai++)
            {
                try
                {
                    File.SetAttributes(ancien, FileAttributes.Normal);
                    File.Delete(ancien);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(1500);
                }
            }
        });
    }
}
