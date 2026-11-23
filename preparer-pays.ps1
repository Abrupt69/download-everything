# Prépare la table des pays que l'application embarque pour dire d'où viennent les pairs d'un torrent.
# Source : la base « IP to Country Lite » de DB-IP, publiée chaque mois sous licence Creative Commons Attribution 4.0,
# qui demande de citer DB-IP avec un lien (fait dans la vue des torrents, dans NOTICES.md et dans le README).
#
# Le fichier du mois est pris à l'adresse officielle de DB-IP, puis comparé aux empreintes SHA-1 et MD5 que sa page de
# téléchargement affiche (DB-IP ne publie pas de SHA-256). Si l'une diffère, le script s'arrête et rien n'est gardé.
# Les 31 Mo du fichier sont ensuite réduits à une table compacte (début de chaque plage d'adresses et son pays,
# plages voisines d'un même pays fusionnées), écrite dans pays\pays-ip.bin.gz. Ce sont des données, jamais exécutées.
#   pwsh -File preparer-pays.ps1
$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
$sortie = Join-Path $ici 'pays'
New-Item -ItemType Directory -Force $sortie | Out-Null
$h = @{ 'User-Agent' = 'Mozilla/5.0 (preparation de donnees)' }

# La page de téléchargement : le lien du fichier CSV du mois, et les deux empreintes affichées juste avant lui.
$page = (Invoke-WebRequest -Uri 'https://db-ip.com/db/download/ip-to-country-lite' -Headers $h -UseBasicParsing).Content
$motif = '(?s)MD5SUM</dt>\s*<dd[^>]*>([0-9a-f]{32})</dd>\s*<dt>SHA1SUM</dt>\s*<dd[^>]*>([0-9a-f]{40})</dd>.{0,400}?href=''(https://download\.db-ip\.com/free/(dbip-country-lite-\d{4}-\d{2})\.csv\.gz)'''
$trouve = [regex]::Match($page, $motif)
if (-not $trouve.Success) { throw "La page de DB-IP n'a pas la forme attendue, le lien ou les empreintes du fichier CSV sont introuvables." }
$md5Attendu, $sha1Attendu, $adresse, $edition = $trouve.Groups[1].Value, $trouve.Groups[2].Value, $trouve.Groups[3].Value, $trouve.Groups[4].Value
"$edition, empreintes publiées SHA-1 $sha1Attendu, MD5 $md5Attendu"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pays-dbip-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $gz = Join-Path $tmp "$edition.csv.gz"
    Invoke-WebRequest -Uri $adresse -OutFile $gz -Headers $h -UseBasicParsing
    # Les empreintes affichées par DB-IP portent sur le fichier une fois décompressé (vérifié le 23/11/2026).
    $csv = Join-Path $tmp "$edition.csv"
    $entree = [IO.Compression.GZipStream]::new([IO.File]::OpenRead($gz), [IO.Compression.CompressionMode]::Decompress)
    $ecriture = [IO.File]::Create($csv)
    try { $entree.CopyTo($ecriture) } finally { $ecriture.Dispose(); $entree.Dispose() }
    $sha1 = (Get-FileHash $csv -Algorithm SHA1).Hash.ToLower()
    $md5 = (Get-FileHash $csv -Algorithm MD5).Hash.ToLower()
    if ($sha1 -ne $sha1Attendu -or $md5 -ne $md5Attendu) { throw "Empreintes du fichier de DB-IP différentes (SHA-1 $sha1, MD5 $md5). Rien n'est gardé." }
    "empreintes vérifiées, {0:N1} Mo reçus, {1:N1} Mo une fois décompressés" -f ((Get-Item $gz).Length / 1MB), ((Get-Item $csv).Length / 1MB)

    # La réduction, écrite en C# pour lire les 730 000 lignes en quelques secondes.
    if (-not ('ReductionDesPays' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.IO; using System.IO.Compression; using System.Net; using System.Text;
public static class ReductionDesPays
{
    // Une plage : son début (IPv4 sur 32 bits, IPv6 sur 128 bits en deux moitiés) et son pays. "" pour un trou.
    sealed class Plage { public ulong Haut, Bas; public string Pays = ""; }

    static void Decouper(IPAddress ip, out ulong haut, out ulong bas)
    {
        var o = ip.GetAddressBytes();
        if (o.Length == 4) { haut = 0; bas = (ulong)o[0] << 24 | (ulong)o[1] << 16 | (ulong)o[2] << 8 | o[3]; return; }
        haut = 0; bas = 0;
        for (var i = 0; i < 8; i++) haut = haut << 8 | o[i];
        for (var i = 8; i < 16; i++) bas = bas << 8 | o[i];
    }

    static void Suivant(ref ulong haut, ref ulong bas) { bas++; if (bas == 0) haut++; }

    public static string Reduire(string source, string cible, string edition)
    {
        var v4 = new List<Plage>(); var v6 = new List<Plage>();
        int lignes = 0;
        ulong[] fin4 = { 0, 0 }, fin6 = { 0, 0 }; bool debut4 = true, debut6 = true;
        using (var lecture = new StreamReader(File.OpenRead(source), Encoding.UTF8))
        {
            string ligne;
            while ((ligne = lecture.ReadLine()) != null)
            {
                var champs = ligne.Split(',');
                if (champs.Length != 3) throw new InvalidDataException("Ligne inattendue : " + ligne);
                var a = IPAddress.Parse(champs[0]); var b = IPAddress.Parse(champs[1]);
                var pays = champs[2].Trim();
                if (pays.Length != 2) throw new InvalidDataException("Code de pays inattendu : " + ligne);
                lignes++;
                ulong ha, ba, hb, bb; Decouper(a, out ha, out ba); Decouper(b, out hb, out bb);
                var liste = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? v4 : v6;
                var fin = liste == v4 ? fin4 : fin6;
                var premier = liste == v4 ? debut4 : debut6;
                // Un trou entre la plage précédente et celle-ci : il est marqué « pays inconnu ».
                ulong attenduH = fin[0], attenduB = fin[1];
                if (!premier) Suivant(ref attenduH, ref attenduB);
                if (premier ? (ha != 0 || ba != 0) : (ha != attenduH || ba != attenduB))
                    if (liste.Count == 0 || liste[liste.Count - 1].Pays != "") liste.Add(new Plage { Haut = premier ? 0 : attenduH, Bas = premier ? 0 : attenduB, Pays = "" });
                // Une plage du même pays que la précédente la prolonge, sans nouvelle entrée.
                if (liste.Count == 0 || liste[liste.Count - 1].Pays != pays) liste.Add(new Plage { Haut = ha, Bas = ba, Pays = pays });
                fin[0] = hb; fin[1] = bb;
                if (liste == v4) debut4 = false; else debut6 = false;
            }
        }
        // Les codes de pays, une fois chacun, « -- » en tête pour l'inconnu.
        var codes = new List<string> { "--" }; var index = new Dictionary<string, ushort> { { "", 0 } };
        foreach (var p in v4) if (!index.ContainsKey(p.Pays)) { index[p.Pays] = (ushort)codes.Count; codes.Add(p.Pays); }
        foreach (var p in v6) if (!index.ContainsKey(p.Pays)) { index[p.Pays] = (ushort)codes.Count; codes.Add(p.Pays); }

        using (var fichier = File.Create(cible))
        using (var zip = new GZipStream(fichier, CompressionLevel.SmallestSize))
        using (var e = new BinaryWriter(zip, Encoding.UTF8))
        {
            e.Write(Encoding.ASCII.GetBytes("DEPAYS1\0"));
            e.Write(edition);
            e.Write(codes.Count); foreach (var c in codes) e.Write(Encoding.ASCII.GetBytes(c));
            e.Write(v4.Count); foreach (var p in v4) e.Write((uint)p.Bas); foreach (var p in v4) e.Write(index[p.Pays]);
            e.Write(v6.Count); foreach (var p in v6) { e.Write(p.Haut); e.Write(p.Bas); } foreach (var p in v6) e.Write(index[p.Pays]);
        }
        return string.Format("{0:N0} lignes lues, {1:N0} plages IPv4 et {2:N0} plages IPv6 après fusion, {3} pays", lignes, v4.Count, v6.Count, codes.Count - 1);
    }
}
'@
    }
    $cible = Join-Path $sortie 'pays-ip.bin.gz'
    [ReductionDesPays]::Reduire($csv, $cible, $edition)
    "table écrite : pays\pays-ip.bin.gz, {0:N0} octets, SHA-256 {1}" -f (Get-Item $cible).Length, (Get-FileHash $cible -Algorithm SHA256).Hash.ToLower()
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
