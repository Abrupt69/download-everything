using System.IO;
using System.Net.Http;

namespace YtDlpInterface;

/// <summary>
/// Dernier recours de la cascade : l'adresse ne mène ni à une vidéo, ni à du son, ni à une galerie d'images,
/// mais simplement à un fichier (un PDF, une archive, un document). Il est alors rapatrié tel quel.
/// Une page web ordinaire n'est pas un fichier, elle est laissée de côté.
/// </summary>
internal static class FichierDirect
{
    // Types de fichiers qui ne sont ni vidéo, ni son, ni image. gallery-dl n'a rien à en faire,
    // inutile de le lancer pour eux.
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".tar", ".tgz", ".exe", ".msi", ".iso", ".img", ".dmg", ".apk",
        ".deb", ".rpm", ".jar", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".epub", ".mobi",
        ".txt", ".csv", ".json", ".xml", ".ttf", ".otf", ".psd",
    };

    public static bool EstUnDocument(string adresse) =>
        Uri.TryCreate(adresse, UriKind.Absolute, out var uri) && Documents.Contains(Path.GetExtension(uri.AbsolutePath));

    /// <summary>
    /// Télécharge le fichier que sert l'adresse et rend son chemin, ou null quand l'adresse ne sert pas un
    /// fichier (réponse d'erreur, ou page web). Le fichier porte le nom que le serveur lui donne, sinon celui
    /// de la fin de l'adresse. Un fichier du même nom déjà présent n'est jamais écrasé.
    /// </summary>
    public static async Task<string?> TelechargerAsync(
        HttpClient client, string adresse, string dossier, IProgress<MiseAJourDesOutils.Avancement> avancement, CancellationToken arret)
    {
        if (!Uri.TryCreate(adresse, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        using var reponse = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, arret);
        if (!reponse.IsSuccessStatusCode) return null;
        var contenu = reponse.Content.Headers;
        var type = contenu.ContentType?.MediaType ?? "";
        var joint = contenu.ContentDisposition is { } disposition
                    && (disposition.FileNameStar ?? disposition.FileName) is { Length: > 0 };
        // Une page web n'est pas un fichier à garder, sauf si le serveur la présente lui-même comme une pièce jointe.
        if (!joint && type is "text/html" or "application/xhtml+xml") return null;

        var finale = reponse.RequestMessage?.RequestUri ?? uri;   // l'adresse après les redirections
        var nom = NomSur(contenu.ContentDisposition?.FileNameStar ?? contenu.ContentDisposition?.FileName?.Trim('"')
                         ?? Uri.UnescapeDataString(Path.GetFileName(finale.AbsolutePath)));
        if (!joint && type.Length == 0 && Path.GetExtension(nom).Length == 0) return null;   // ni type ni extension, rien ne dit que c'est un fichier

        Directory.CreateDirectory(dossier);
        var cible = CheminLibre(dossier, nom);
        var partiel = cible + ".partiel";
        var total = contenu.ContentLength ?? 0;
        try
        {
            await using (var source = await reponse.Content.ReadAsStreamAsync(arret))
            await using (var sortie = new FileStream(partiel, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var tampon = new byte[1 << 16];
                long recu = 0;
                var dernierRapport = 0L;
                int lu;
                while ((lu = await source.ReadAsync(tampon, arret)) > 0)
                {
                    await sortie.WriteAsync(tampon.AsMemory(0, lu), arret);
                    recu += lu;
                    if (Environment.TickCount64 - dernierRapport < 250) continue;
                    dernierRapport = Environment.TickCount64;
                    avancement.Report(new MiseAJourDesOutils.Avancement($"Téléchargement de {nom}…", recu, total));
                }
                avancement.Report(new MiseAJourDesOutils.Avancement($"Téléchargement de {nom}…", recu, total > 0 ? total : recu));
            }
            File.Move(partiel, cible);
            return cible;
        }
        catch
        {
            // Annulé ou coupé en route : le morceau reçu ne reste pas dans le dossier sous un nom trompeur.
            try { File.Delete(partiel); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Un nom de fichier sans caractère interdit ni chemin, quel que soit ce que le serveur a envoyé.</summary>
    private static string NomSur(string propose)
    {
        var nom = Path.GetFileName(propose.Replace('\\', '/').TrimEnd('/'));
        foreach (var interdit in Path.GetInvalidFileNameChars()) nom = nom.Replace(interdit, '-');
        nom = nom.Trim().TrimEnd('.', ' ');
        if (nom.Length > 150) nom = nom[..(150 - Path.GetExtension(nom).Length)].TrimEnd('.', ' ') + Path.GetExtension(nom);
        return nom.Length > 0 ? nom : "fichier";
    }

    /// <summary>« rapport.pdf », ou « rapport (2).pdf » si le premier nom est déjà pris.</summary>
    private static string CheminLibre(string dossier, string nom)
    {
        var chemin = Path.Combine(dossier, nom);
        for (var rang = 2; File.Exists(chemin) && rang < 1000; rang++)
            chemin = Path.Combine(dossier, $"{Path.GetFileNameWithoutExtension(nom)} ({rang}){Path.GetExtension(nom)}");
        return chemin;
    }
}
