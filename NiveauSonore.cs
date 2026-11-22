using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YtDlpInterface;

/// <summary>
/// Niveau sonore égalisé, pour enchaîner des morceaux dans un set sans retoucher le gain à chacun.
///
/// Il n'existe pas de norme officielle pour les sets de DJ. Deux repères ont guidé les choix faits ici, relevés
/// le 22/11/2026. La musique de club est masterisée entre −6 et −9 LUFS, bien plus fort que les −14 LUFS des
/// plateformes d'écoute. Et les outils de DJ égalisent tous de la même façon : un seul gain par morceau,
/// appliqué d'un bloc, sans compresseur ni limiteur, pour ne rien changer à la dynamique.
///
/// D'où la règle. Chaque morceau est mesuré (norme EBU R128), puis ramené vers −9 LUFS, le bas de la fourchette
/// des morceaux de club. Un morceau plus fort est baissé. Un morceau plus calme n'est monté que tant que sa
/// crête reste sous −1 dB : au-delà il saturerait, il est alors laissé tel quel, et le journal le dit.
///
/// Le son n'est jamais réencodé avec perte. Un MP3 porte dans chacune de ses trames un réglage de volume, par
/// crans de 1,5 dB : c'est lui qui est déplacé, comme le fait MP3Gain depuis vingt ans. Les formats sans perte
/// (FLAC, WAV, AIFF, ALAC) sont réécrits par ffmpeg avec le gain, sans perte non plus.
/// </summary>
internal static partial class NiveauSonore
{
    /// <summary>Le niveau visé, en LUFS intégrés.</summary>
    public const double Cible = -9;

    /// <summary>La crête vraie à ne pas dépasser en montant un morceau, en dB.</summary>
    public const double Plafond = -1;

    // Un cran du réglage de volume d'une trame MP3 vaut 1,5 dB. Le niveau final tombe donc à 0,75 dB près.
    private const double CranMp3 = 1.5;

    private static readonly CultureInfo Francais = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Les formats que l'on sait égaliser sans perte. Les autres (M4A, AAC, Opus, Vorbis) ne sont pas touchés.</summary>
    public static bool PrendEnCharge(string format) => format is "mp3" or "flac" or "wav" or "aiff" or "alac";

    /// <summary>Le niveau d'un morceau (LUFS intégrés) et sa crête vraie (dB).</summary>
    internal sealed record Mesure(double Niveau, double Crete);

    [GeneratedRegex("\"input_i\"\\s*:\\s*\"(-?[0-9.]+)\"")]
    private static partial Regex NiveauMesure();

    [GeneratedRegex("\"input_tp\"\\s*:\\s*\"(-?[0-9.]+)\"")]
    private static partial Regex CreteMesuree();

    /// <summary>
    /// Mesure un morceau, égalise son niveau si c'est possible sans rien abîmer, et rend la phrase à écrire au
    /// journal. Rend null quand le format n'est pas pris en charge : le fichier n'est alors pas touché.
    /// </summary>
    /// <param name="suivre">Reçoit chaque ffmpeg lancé, pour qu'une annulation puisse l'arrêter.</param>
    public static async Task<string?> EgaliserAsync(string ffmpeg, string fichier, string format, Action<Process?> suivre)
    {
        if (!PrendEnCharge(format) || !ExtensionAttendue(format, fichier)) return null;

        var mesure = await MesurerAsync(ffmpeg, fichier, suivre);
        if (mesure is null) return "niveau non mesurable, le fichier est laissé tel quel.";

        var gain = GainAAppliquer(mesure, format == "mp3" ? CranMp3 : 0);
        var avant = mesure.Niveau.ToString("0.0", Francais);
        if (gain == 0)
        {
            return mesure.Niveau < Cible - CranMp3 / 2
                ? $"{avant} LUFS, laissé tel quel. Le monter le ferait saturer (crête à {mesure.Crete.ToString("0.0", Francais)} dB)."
                : $"{avant} LUFS, déjà au niveau.";
        }

        var fait = format == "mp3"
            ? DecalerLeGainMp3(fichier, (int)Math.Round(gain / CranMp3))
            : await ReecrireSansPerteAsync(ffmpeg, fichier, format, gain, suivre);
        if (!fait) return $"{avant} LUFS, laissé tel quel. Le fichier n'a pas pu être égalisé sans risque.";

        var apres = (mesure.Niveau + gain).ToString("0.0", Francais);
        return $"{avant} LUFS ramené à {apres} LUFS ({gain.ToString("+0.0;-0.0", Francais)} dB, sans réencodage avec perte).";
    }

    private static bool ExtensionAttendue(string format, string fichier)
    {
        var extension = Path.GetExtension(fichier).ToLowerInvariant();
        return format switch
        {
            "mp3" => extension == ".mp3",
            "flac" => extension == ".flac",
            "wav" => extension == ".wav",
            "aiff" => extension is ".aiff" or ".aif",
            "alac" => extension == ".m4a",
            _ => false,
        };
    }

    /// <summary>
    /// Le gain à appliquer, en dB. Baisser est toujours permis. Monter ne l'est que tant que la crête reste
    /// sous le plafond. Avec un pas (le MP3), le gain est arrondi au cran, vers le bas quand on monte pour ne
    /// jamais franchir le plafond.
    /// </summary>
    internal static double GainAAppliquer(Mesure mesure, double pas)
    {
        var voulu = Cible - mesure.Niveau;
        if (voulu > 0) voulu = Math.Min(voulu, Math.Max(0, Plafond - mesure.Crete));
        if (pas > 0) return (voulu > 0 ? Math.Floor(voulu / pas) : Math.Round(voulu / pas)) * pas;
        // Sans pas, un écart de moins d'un dixième de dB ne vaut pas une réécriture.
        return Math.Abs(voulu) < 0.1 ? 0 : Math.Round(voulu, 2);
    }

    /// <summary>Fait mesurer le morceau par ffmpeg (filtre loudnorm, norme EBU R128). Rend null si la mesure échoue ou si le morceau est muet.</summary>
    internal static async Task<Mesure?> MesurerAsync(string ffmpeg, string fichier, Action<Process?> suivre)
    {
        var (code, sortie) = await LancerAsync(ffmpeg,
            ["-hide_banner", "-nostats", "-i", fichier, "-map", "0:a:0", "-af", "loudnorm=print_format=json", "-f", "null", "-"], suivre);
        if (code != 0) return null;
        // Un morceau muet donne « -inf », que les deux motifs ne reconnaissent pas : il n'y a alors rien à égaliser.
        var niveau = NiveauMesure().Matches(sortie);
        var crete = CreteMesuree().Matches(sortie);
        if (niveau.Count == 0 || crete.Count == 0) return null;
        return new Mesure(
            double.Parse(niveau[^1].Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(crete[^1].Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Réécrit un fichier sans perte avec le gain voulu, étiquettes et jaquette reprises. Le résultat est d'abord
    /// écrit à côté, et ne remplace l'original qu'une fois complet.
    /// </summary>
    private static async Task<bool> ReecrireSansPerteAsync(string ffmpeg, string fichier, string format, double gain, Action<Process?> suivre)
    {
        var provisoire = Path.Combine(Path.GetDirectoryName(fichier)!, Path.GetFileNameWithoutExtension(fichier) + ".niveau" + Path.GetExtension(fichier));
        var arguments = new List<string> { "-hide_banner", "-nostats", "-y", "-i", fichier, "-map_metadata", "0" };
        // Le FLAC et l'ALAC portent leur jaquette comme une image jointe, qui suit telle quelle. Le WAV et l'AIFF n'en ont pas.
        arguments.AddRange(format switch
        {
            "flac" => ["-map", "0", "-c:v", "copy", "-disposition:v", "attached_pic", "-c:a", "flac"],
            "alac" => ["-map", "0", "-c:v", "copy", "-disposition:v", "attached_pic", "-c:a", "alac"],
            "aiff" => ["-map", "0:a", "-write_id3v2", "1"],
            _ => ["-map", "0:a"],
        });
        arguments.AddRange(["-af", $"volume={gain.ToString("0.00", CultureInfo.InvariantCulture)}dB", provisoire]);
        try
        {
            var (code, _) = await LancerAsync(ffmpeg, arguments, suivre);
            if (code != 0 || !File.Exists(provisoire) || new FileInfo(provisoire).Length == 0) return false;
            File.Move(provisoire, fichier, overwrite: true);
            return true;
        }
        finally
        {
            // Arrêté en route ou en échec : rien ne reste à côté du morceau.
            if (File.Exists(provisoire)) File.Delete(provisoire);
        }
    }

    private static async Task<(int Code, string Sortie)> LancerAsync(string ffmpeg, IEnumerable<string> arguments, Action<Process?> suivre)
    {
        var demarrage = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) demarrage.ArgumentList.Add(argument);
        using var processus = new Process { StartInfo = demarrage };
        processus.Start();
        suivre(processus);
        try
        {
            // Les deux canaux sont lus en même temps, sinon ffmpeg se bloquerait en écrivant dans celui qu'on ne vide pas.
            var erreurs = processus.StandardError.ReadToEndAsync();
            var sorties = processus.StandardOutput.ReadToEndAsync();
            await processus.WaitForExitAsync();
            await sorties;
            return (processus.ExitCode, await erreurs);
        }
        finally
        {
            suivre(null);
        }
    }

    // ───────────────────────────── MP3 : le réglage de volume des trames ─────────────────────────────

    // Débits des trames de couche III, en kb/s, par indice. La première ligne pour MPEG-1, la seconde pour MPEG-2 et 2.5.
    private static readonly int[][] Debits =
    [
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    // Fréquences d'échantillonnage par indice, pour MPEG-1, MPEG-2 et MPEG-2.5.
    private static readonly int[][] Frequences = [[44100, 48000, 32000], [22050, 24000, 16000], [11025, 12000, 8000]];

    /// <summary>
    /// Déplace le réglage de volume de toutes les trames d'un MP3, d'un nombre de crans de 1,5 dB, sans toucher
    /// au son encodé. Le fichier doit être compris de bout en bout : au moindre doute, rien n'est écrit et la
    /// méthode rend faux. Les étiquettes et la jaquette, hors des trames, ne bougent pas.
    /// </summary>
    internal static bool DecalerLeGainMp3(string fichier, int crans)
    {
        if (crans == 0) return true;
        var octets = File.ReadAllBytes(fichier);
        var position = 0;

        // Les étiquettes ID3v2 de tête (titre, artiste, jaquette) sont sautées. Leur taille s'écrit sur 4 × 7 bits.
        while (position + 10 <= octets.Length && octets[position] == 'I' && octets[position + 1] == 'D' && octets[position + 2] == '3')
        {
            var taille = ((octets[position + 6] & 0x7F) << 21) | ((octets[position + 7] & 0x7F) << 14)
                         | ((octets[position + 8] & 0x7F) << 7) | (octets[position + 9] & 0x7F);
            position += 10 + taille + ((octets[position + 5] & 0x10) != 0 ? 10 : 0);
        }

        var trames = 0;
        while (position + 4 <= octets.Length && octets[position] == 0xFF && (octets[position + 1] & 0xE0) == 0xE0)
        {
            var version = (octets[position + 1] >> 3) & 3;      // 3 : MPEG-1, 2 : MPEG-2, 0 : MPEG-2.5
            var couche = (octets[position + 1] >> 1) & 3;       // 1 : couche III
            var sansControle = (octets[position + 1] & 1) == 1;
            var indiceDebit = octets[position + 2] >> 4;
            var indiceFrequence = (octets[position + 2] >> 2) & 3;
            var bourrage = (octets[position + 2] >> 1) & 1;
            var mono = (octets[position + 3] >> 6) == 3;
            // Autre chose qu'une trame de couche III ordinaire : on ne sait pas la lire, on n'écrit rien.
            if (version == 1 || couche != 1 || indiceDebit is 0 or 15 || indiceFrequence == 3) return false;

            var mpeg1 = version == 3;
            var frequence = Frequences[mpeg1 ? 0 : version == 2 ? 1 : 2][indiceFrequence];
            var longueur = (mpeg1 ? 144 : 72) * Debits[mpeg1 ? 0 : 1][indiceDebit] * 1000 / frequence + bourrage;
            if (position + longueur > octets.Length) break;   // dernière trame coupée, on s'arrête avant

            var infos = position + 4 + (sansControle ? 0 : 2);
            var tailleDesInfos = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            // La trame d'en-tête de LAME (« Xing » ou « Info ») ne porte pas de son, elle est laissée telle quelle.
            var marque = infos + tailleDesInfos;
            var enTeteDeLame = marque + 4 <= octets.Length
                               && Encoding.ASCII.GetString(octets, marque, 4) is "Xing" or "Info";
            if (!enTeteDeLame)
            {
                var canaux = mono ? 1 : 2;
                // Dans les informations de la trame, le réglage de volume (8 bits) suit de 21 bits le début de chaque bloc.
                var debut = mpeg1 ? 9 + (mono ? 5 : 3) + 4 * canaux : 8 + (mono ? 1 : 2);
                var bloc = mpeg1 ? 59 : 63;
                for (var i = 0; i < (mpeg1 ? 2 : 1) * canaux; i++)
                {
                    var bit = infos * 8 + debut + i * bloc + 21;
                    EcrireBits(octets, bit, 8, Math.Clamp(LireBits(octets, bit, 8) + crans, 0, 255));
                }
                // Une trame protégée porte une somme de contrôle de ses informations, à recalculer.
                if (!sansControle)
                {
                    var somme = Controle(octets, position + 2, 2, 0xFFFF);
                    somme = Controle(octets, infos, tailleDesInfos, somme);
                    octets[position + 4] = (byte)(somme >> 8);
                    octets[position + 5] = (byte)somme;
                }
            }
            position += longueur;
            trames++;
        }

        // Tout doit avoir été lu, à une étiquette de fin près (ID3v1, APE). Sinon le fichier n'est pas ce qu'on croit.
        var reste = octets.Length - position;
        if (trames < 10 || reste > Math.Max(4096, octets.Length / 200)) return false;

        var provisoire = fichier + ".niveau";
        try
        {
            File.WriteAllBytes(provisoire, octets);
            File.Move(provisoire, fichier, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(provisoire)) File.Delete(provisoire);
        }
    }

    private static int LireBits(byte[] octets, int bit, int nombre)
    {
        var valeur = 0;
        for (var i = 0; i < nombre; i++, bit++)
            valeur = (valeur << 1) | ((octets[bit >> 3] >> (7 - (bit & 7))) & 1);
        return valeur;
    }

    private static void EcrireBits(byte[] octets, int bit, int nombre, int valeur)
    {
        for (var i = nombre - 1; i >= 0; i--, bit++)
        {
            var masque = (byte)(1 << (7 - (bit & 7)));
            if (((valeur >> i) & 1) == 1) octets[bit >> 3] |= masque;
            else octets[bit >> 3] &= (byte)~masque;
        }
    }

    /// <summary>La somme de contrôle des trames MP3 (CRC-16, polynôme 0x8005), poursuivie sur une plage d'octets.</summary>
    private static int Controle(byte[] octets, int debut, int nombre, int somme)
    {
        for (var i = debut; i < debut + nombre; i++)
        {
            somme ^= octets[i] << 8;
            for (var b = 0; b < 8; b++)
                somme = (somme & 0x8000) != 0 ? ((somme << 1) ^ 0x8005) & 0xFFFF : (somme << 1) & 0xFFFF;
        }
        return somme;
    }
}
