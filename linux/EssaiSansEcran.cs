using System.Diagnostics;
using System.Net.Http;

namespace YtDlpInterface;

/// <summary>
/// Mode d'essai sans fenêtre, pour vérifier le moteur sous Linux dans un conteneur, là où il n'y a ni écran ni souris.
///   download-everything --essai outils
///   download-everything --essai telecharger ADRESSE DOSSIER [audio]
/// Il déroule la même cascade que la fenêtre (yt-dlp, puis gallery-dl, puis le fichier tel quel) et écrit
/// ce qui se passe sur la sortie. Ce n'est pas un mode d'emploi prévu pour l'utilisateur.
/// </summary>
internal static class EssaiSansEcran
{
    public static async Task<int> LancerAsync(string[] args)
    {
        try
        {
            return args switch
            {
                [_, "outils"] => await OutilsAsync(),
                [_, "telecharger", var adresse, var dossier, ..] => await TelechargerAsync(adresse, dossier, args.Contains("audio")),
                [_, "torrent", var adresse, var dossier, ..] => await TorrentAsync(adresse, dossier),
                [_, "doutes", var fichier, ..] => Doutes(fichier),
                _ => Dire(2, "Usage : --essai outils | --essai telecharger ADRESSE DOSSIER [audio] | --essai torrent ADRESSE DOSSIER"),
            };
        }
        catch (Exception e)
        {
            return Dire(1, $"ECHEC {e.GetType().Name} : {e.Message}");
        }
    }

    private static int Dire(int code, string message)
    {
        Console.WriteLine(message);
        return code;
    }

    private static async Task<int> OutilsAsync()
    {
        Console.WriteLine($"dossier des outils : {YtDlp.DossierOutils}");
        using var client = MiseAJourDesOutils.NouveauClient();
        var muet = new Progress<MiseAJourDesOutils.Avancement>(_ => { });
        foreach (var (nom, tache) in MiseAJourDesOutils.OutilsLinux)
            Console.WriteLine($"{nom} : {await tache(client, muet, CancellationToken.None)}");

        var complements = YtDlp.TrouverComplements();
        Console.WriteLine($"yt-dlp     : {YtDlp.Trouver() ?? "ABSENT"}, version {(YtDlp.Trouver() is { } y ? await YtDlp.LireVersionAsync(y) : null) ?? "?"}");
        Console.WriteLine($"ffmpeg     : {complements.Ffmpeg ?? "ABSENT"}");
        Console.WriteLine($"deno       : {complements.Deno ?? "ABSENT"}");
        Console.WriteLine($"gallery-dl : {GalleryDl.Trouver() ?? "ABSENT"}, version {(GalleryDl.Trouver() is { } g ? await GalleryDl.LireVersionAsync(g) : null) ?? "?"}");
        Console.WriteLine($"aria2      : {Aria2.Trouver() ?? "ABSENT"}");
        Console.WriteLine($"versions notées : {string.Join(", ", MiseAJourDesOutils.LireVersions().Select(v => $"{v.Key}={v.Value}"))}");
        return 0;
    }

    private static async Task<int> TelechargerAsync(string adresse, string dossier, bool audio)
    {
        Directory.CreateDirectory(dossier);
        var executable = YtDlp.Trouver() ?? throw new FileNotFoundException("yt-dlp manque, lancer d'abord --essai outils");
        var complements = YtDlp.TrouverComplements();

        // Dernier étage d'abord pour les documents, comme dans la fenêtre.
        if (!FichierDirect.EstUnDocument(adresse))
        {
            var demande = new Demande(adresse, audio, audio ? "mp3" : "mp4", "", dossier, PrefixeDeNom: "");
            YtDlp.EffacerLaTrace();
            var (code, lignes) = await LancerAsync(YtDlp.Preparer(executable, YtDlp.ArgumentsTelechargement(demande, complements)));
            var recus = YtDlp.LireLaTrace();
            Console.WriteLine($"yt-dlp : code {code}, {recus.Count} fichier(s)");
            foreach (var ligne in lignes.Where(l => l.StartsWith("ERROR", StringComparison.Ordinal) || l.Contains("does not pass filter")).Take(3)) Console.WriteLine("  " + ligne);
            if (recus.Count > 0) return Dire(0, "RECU par yt-dlp : " + string.Join(" | ", recus));

            if (!audio && GalleryDl.Trouver() is { } galerie)
            {
                var (codeGalerie, sortie) = await LancerAsync(GalleryDl.Preparer(galerie, GalleryDl.Arguments(adresse, dossier)));
                var images = sortie.Select(l => GalleryDl.LireUneLigne(l).Fichier).OfType<string>().Distinct().ToList();
                Console.WriteLine($"gallery-dl : code {codeGalerie}, {images.Count} fichier(s)");
                if (images.Count > 0) return Dire(0, "RECU par gallery-dl : " + string.Join(" | ", images));
            }
        }

        using var client = MiseAJourDesOutils.NouveauClient();
        var fichier = await FichierDirect.TelechargerAsync(client, adresse, dossier, new Progress<MiseAJourDesOutils.Avancement>(_ => { }), CancellationToken.None);
        return fichier is null ? Dire(1, "RIEN : aucun des trois étages n'a rien pris") : Dire(0, "RECU tel quel : " + fichier);
    }

    /// <summary>
    /// Rejoue le garde-fou des titres sur des cas réels : une ligne par morceau, le titre attendu, une tabulation,
    /// puis le titre de la vidéo qui avait été retenue. Écrit ceux qui seraient signalés.
    /// </summary>
    private static int Doutes(string fichier)
    {
        var signales = 0;
        var lignes = File.ReadAllLines(fichier).Where(l => l.Contains('\t')).ToList();
        foreach (var ligne in lignes)
        {
            var (attendu, trouve) = (ligne.Split('\t')[0], ligne.Split('\t')[1]);
            if (YtDlp.DouteSurLeTitre(attendu, trouve) is not { } raison) continue;
            signales++;
            Console.WriteLine($"A VERIFIER : {attendu}  ->  {trouve}  ({raison})");
        }
        Console.WriteLine($"{signales} signalé(s) sur {lignes.Count}");
        return 0;
    }

    /// <summary>Le même chemin que la vue des torrents : aria2 lancé en service, le torrent ajouté, puis suivi chaque seconde.</summary>
    private static async Task<int> TorrentAsync(string adresse, string dossier)
    {
        Directory.CreateDirectory(dossier);
        var service = new ServiceTorrents();
        try
        {
            await service.DemarrerAsync();
            Console.WriteLine($"aria2 {service.Version} en service");
            using var client = MiseAJourDesOutils.NouveauClient();
            var (magnet, fichier) = await Aria2.LireLaSourceAsync(adresse, client, CancellationToken.None);
            await service.AjouterAsync(magnet, fichier, dossier);
            for (var seconde = 0; seconde < 150; seconde++)
            {
                await Task.Delay(1000);
                if ((await service.ListerAsync()).FirstOrDefault(t => !t.Suivi) is not { } torrent) continue;
                if (seconde % 10 == 0)
                    Console.WriteLine($"  {seconde,3} s : {torrent.Statut}, {torrent.Recu:0} sur {torrent.Total:0} octets, {torrent.Liaisons} liaison(s), {torrent.Sources} source(s)");
                if (torrent.Statut == "complete") return Dire(0, $"TORRENT RECU : {torrent.Nom}, {torrent.Fichiers.Count} fichier(s), en {seconde} s");
                if (torrent.Statut == "error") return Dire(1, $"TORRENT EN ERREUR : {torrent.Erreur}");
            }
            return Dire(1, "TORRENT NON TERMINE en 150 secondes");
        }
        finally
        {
            service.Arreter();
        }
    }

    private static async Task<(int Code, List<string> Lignes)> LancerAsync(ProcessStartInfo demarrage)
    {
        var lignes = new List<string>();
        using var processus = new Process { StartInfo = demarrage };
        processus.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lignes) lignes.Add(e.Data); };
        processus.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lignes) lignes.Add(e.Data); };
        processus.Start();
        processus.BeginOutputReadLine();
        processus.BeginErrorReadLine();
        await processus.WaitForExitAsync();
        return (processus.ExitCode, lignes);
    }
}