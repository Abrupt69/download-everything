using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YtDlpInterface;

/// <summary>
/// Le pays d'une adresse IP, pour dire d'où viennent les pairs d'un torrent. Tout se passe sur le poste : la table
/// voyage dans l'exécutable (ressource « pays-ip.bin.gz », préparée par preparer-pays.ps1 à partir de la base
/// « IP to Country Lite » de DB-IP, licence CC BY 4.0) et aucune adresse n'est envoyée nulle part.
///
/// La table donne, pour IPv4 puis pour IPv6, le début de chaque plage d'adresses dans l'ordre et son pays. Le pays
/// d'une adresse est celui de la dernière plage qui commence avant elle : une recherche par dichotomie, instantanée.
/// </summary>
internal static class PaysDesAdresses
{
    private const string Ressource = "pays-ip.bin.gz";

    private static readonly object Verrou = new();
    private static bool _chargee;
    private static string[] _codes = [];
    private static uint[] _debuts4 = [];
    private static ushort[] _pays4 = [];
    private static ulong[] _hauts6 = [], _bas6 = [];
    private static ushort[] _pays6 = [];

    /// <summary>L'édition de la base embarquée, « dbip-country-lite-2026-11 » par exemple. Vide sans table.</summary>
    public static string Edition { get; private set; } = "";

    /// <summary>Vrai quand une table est embarquée. Sans elle (compilation sans preparer-pays.ps1), la colonne reste vide.</summary>
    public static bool Disponible
    {
        get
        {
            Charger();
            return _debuts4.Length > 0;
        }
    }

    /// <summary>Le code ISO du pays d'une adresse (« FR »), ou null quand il est inconnu.</summary>
    public static string? Code(string adresse)
    {
        Charger();
        if (!IPAddress.TryParse(adresse, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var octets = ip.GetAddressBytes();
        int rang;
        ushort index;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (_debuts4.Length == 0) return null;
            var valeur = (uint)octets[0] << 24 | (uint)octets[1] << 16 | (uint)octets[2] << 8 | octets[3];
            rang = Array.BinarySearch(_debuts4, valeur);
            if (rang < 0) rang = ~rang - 1;
            if (rang < 0) return null;
            index = _pays4[rang];
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (_hauts6.Length == 0) return null;
            ulong haut = 0, bas = 0;
            for (var i = 0; i < 8; i++) haut = haut << 8 | octets[i];
            for (var i = 8; i < 16; i++) bas = bas << 8 | octets[i];
            rang = DerniereAvant6(haut, bas);
            if (rang < 0) return null;
            index = _pays6[rang];
        }
        else return null;
        var code = index < _codes.Length ? _codes[index] : "--";
        // « -- » : un trou dans la base. « ZZ » : une adresse réservée, sans pays.
        return code is "--" or "ZZ" ? null : code;
    }

    /// <summary>Le nom du pays d'une adresse, en français quand le système le permet (« France »), ou null s'il est inconnu.</summary>
    public static string? Nom(string adresse) => Code(adresse) is { } code ? NomDuPays(code) : null;

    // Les noms des pays en français, pour les codes que contient la base. Ils sont écrits ici plutôt que demandés au
    // système : vérifié le 23/11/2026, celui-ci rend un mélange d'anglais et de noms d'origine (« United States »,
    // « Deutschland ») même sur un poste en français.
    private static readonly Dictionary<string, string> NomsFrancais = new(StringComparer.Ordinal)
    {
        ["AD"] = "Andorre", ["AE"] = "Émirats arabes unis", ["AF"] = "Afghanistan", ["AG"] = "Antigua-et-Barbuda", ["AI"] = "Anguilla",
        ["AL"] = "Albanie", ["AM"] = "Arménie", ["AO"] = "Angola", ["AQ"] = "Antarctique", ["AR"] = "Argentine", ["AS"] = "Samoa américaines",
        ["AT"] = "Autriche", ["AU"] = "Australie", ["AW"] = "Aruba", ["AX"] = "Îles Åland", ["AZ"] = "Azerbaïdjan",
        ["BA"] = "Bosnie-Herzégovine", ["BB"] = "Barbade", ["BD"] = "Bangladesh", ["BE"] = "Belgique", ["BF"] = "Burkina Faso",
        ["BG"] = "Bulgarie", ["BH"] = "Bahreïn", ["BI"] = "Burundi", ["BJ"] = "Bénin", ["BL"] = "Saint-Barthélemy", ["BM"] = "Bermudes",
        ["BN"] = "Brunei", ["BO"] = "Bolivie", ["BQ"] = "Pays-Bas caribéens", ["BR"] = "Brésil", ["BS"] = "Bahamas", ["BT"] = "Bhoutan",
        ["BV"] = "Île Bouvet", ["BW"] = "Botswana", ["BY"] = "Biélorussie", ["BZ"] = "Belize",
        ["CA"] = "Canada", ["CC"] = "Îles Cocos", ["CD"] = "Congo-Kinshasa", ["CF"] = "Centrafrique", ["CG"] = "Congo-Brazzaville",
        ["CH"] = "Suisse", ["CI"] = "Côte d'Ivoire", ["CK"] = "Îles Cook", ["CL"] = "Chili", ["CM"] = "Cameroun", ["CN"] = "Chine",
        ["CO"] = "Colombie", ["CR"] = "Costa Rica", ["CU"] = "Cuba", ["CV"] = "Cap-Vert", ["CW"] = "Curaçao", ["CX"] = "Île Christmas",
        ["CY"] = "Chypre", ["CZ"] = "Tchéquie",
        ["DE"] = "Allemagne", ["DJ"] = "Djibouti", ["DK"] = "Danemark", ["DM"] = "Dominique", ["DO"] = "République dominicaine", ["DZ"] = "Algérie",
        ["EC"] = "Équateur", ["EE"] = "Estonie", ["EG"] = "Égypte", ["EH"] = "Sahara occidental", ["ER"] = "Érythrée", ["ES"] = "Espagne",
        ["ET"] = "Éthiopie",
        ["FI"] = "Finlande", ["FJ"] = "Fidji", ["FK"] = "Îles Malouines", ["FM"] = "Micronésie", ["FO"] = "Îles Féroé", ["FR"] = "France",
        ["GA"] = "Gabon", ["GB"] = "Royaume-Uni", ["GD"] = "Grenade", ["GE"] = "Géorgie", ["GF"] = "Guyane", ["GG"] = "Guernesey",
        ["GH"] = "Ghana", ["GI"] = "Gibraltar", ["GL"] = "Groenland", ["GM"] = "Gambie", ["GN"] = "Guinée", ["GP"] = "Guadeloupe",
        ["GQ"] = "Guinée équatoriale", ["GR"] = "Grèce", ["GS"] = "Géorgie du Sud", ["GT"] = "Guatemala", ["GU"] = "Guam",
        ["GW"] = "Guinée-Bissau", ["GY"] = "Guyana",
        ["HK"] = "Hong Kong", ["HM"] = "Îles Heard-et-MacDonald", ["HN"] = "Honduras", ["HR"] = "Croatie", ["HT"] = "Haïti", ["HU"] = "Hongrie",
        ["ID"] = "Indonésie", ["IE"] = "Irlande", ["IL"] = "Israël", ["IM"] = "Île de Man", ["IN"] = "Inde", ["IO"] = "Territoire britannique de l'océan Indien",
        ["IQ"] = "Irak", ["IR"] = "Iran", ["IS"] = "Islande", ["IT"] = "Italie",
        ["JE"] = "Jersey", ["JM"] = "Jamaïque", ["JO"] = "Jordanie", ["JP"] = "Japon",
        ["KE"] = "Kenya", ["KG"] = "Kirghizistan", ["KH"] = "Cambodge", ["KI"] = "Kiribati", ["KM"] = "Comores", ["KN"] = "Saint-Christophe-et-Niévès",
        ["KP"] = "Corée du Nord", ["KR"] = "Corée du Sud", ["KW"] = "Koweït", ["KY"] = "Îles Caïmans", ["KZ"] = "Kazakhstan",
        ["LA"] = "Laos", ["LB"] = "Liban", ["LC"] = "Sainte-Lucie", ["LI"] = "Liechtenstein", ["LK"] = "Sri Lanka", ["LR"] = "Liberia",
        ["LS"] = "Lesotho", ["LT"] = "Lituanie", ["LU"] = "Luxembourg", ["LV"] = "Lettonie", ["LY"] = "Libye",
        ["MA"] = "Maroc", ["MC"] = "Monaco", ["MD"] = "Moldavie", ["ME"] = "Monténégro", ["MF"] = "Saint-Martin", ["MG"] = "Madagascar",
        ["MH"] = "Îles Marshall", ["MK"] = "Macédoine du Nord", ["ML"] = "Mali", ["MM"] = "Birmanie", ["MN"] = "Mongolie", ["MO"] = "Macao",
        ["MP"] = "Îles Mariannes du Nord", ["MQ"] = "Martinique", ["MR"] = "Mauritanie", ["MS"] = "Montserrat", ["MT"] = "Malte",
        ["MU"] = "Maurice", ["MV"] = "Maldives", ["MW"] = "Malawi", ["MX"] = "Mexique", ["MY"] = "Malaisie", ["MZ"] = "Mozambique",
        ["NA"] = "Namibie", ["NC"] = "Nouvelle-Calédonie", ["NE"] = "Niger", ["NF"] = "Île Norfolk", ["NG"] = "Nigeria", ["NI"] = "Nicaragua",
        ["NL"] = "Pays-Bas", ["NO"] = "Norvège", ["NP"] = "Népal", ["NR"] = "Nauru", ["NU"] = "Niue", ["NZ"] = "Nouvelle-Zélande",
        ["OM"] = "Oman",
        ["PA"] = "Panama", ["PE"] = "Pérou", ["PF"] = "Polynésie française", ["PG"] = "Papouasie-Nouvelle-Guinée", ["PH"] = "Philippines",
        ["PK"] = "Pakistan", ["PL"] = "Pologne", ["PM"] = "Saint-Pierre-et-Miquelon", ["PN"] = "Îles Pitcairn", ["PR"] = "Porto Rico",
        ["PS"] = "Palestine", ["PT"] = "Portugal", ["PW"] = "Palaos", ["PY"] = "Paraguay",
        ["QA"] = "Qatar",
        ["RE"] = "La Réunion", ["RO"] = "Roumanie", ["RS"] = "Serbie", ["RU"] = "Russie", ["RW"] = "Rwanda",
        ["SA"] = "Arabie saoudite", ["SB"] = "Îles Salomon", ["SC"] = "Seychelles", ["SD"] = "Soudan", ["SE"] = "Suède", ["SG"] = "Singapour",
        ["SH"] = "Sainte-Hélène", ["SI"] = "Slovénie", ["SJ"] = "Svalbard et Jan Mayen", ["SK"] = "Slovaquie", ["SL"] = "Sierra Leone",
        ["SM"] = "Saint-Marin", ["SN"] = "Sénégal", ["SO"] = "Somalie", ["SR"] = "Suriname", ["SS"] = "Soudan du Sud",
        ["ST"] = "Sao Tomé-et-Principe", ["SV"] = "Salvador", ["SX"] = "Saint-Martin (partie néerlandaise)", ["SY"] = "Syrie", ["SZ"] = "Eswatini",
        ["TC"] = "Îles Turques-et-Caïques", ["TD"] = "Tchad", ["TF"] = "Terres australes françaises", ["TG"] = "Togo", ["TH"] = "Thaïlande",
        ["TJ"] = "Tadjikistan", ["TK"] = "Tokelau", ["TL"] = "Timor oriental", ["TM"] = "Turkménistan", ["TN"] = "Tunisie", ["TO"] = "Tonga",
        ["TR"] = "Turquie", ["TT"] = "Trinité-et-Tobago", ["TV"] = "Tuvalu", ["TW"] = "Taïwan", ["TZ"] = "Tanzanie",
        ["UA"] = "Ukraine", ["UG"] = "Ouganda", ["UM"] = "Îles mineures éloignées des États-Unis", ["US"] = "États-Unis", ["UY"] = "Uruguay",
        ["UZ"] = "Ouzbékistan",
        ["VA"] = "Vatican", ["VC"] = "Saint-Vincent-et-les-Grenadines", ["VE"] = "Venezuela", ["VG"] = "Îles Vierges britanniques",
        ["VI"] = "Îles Vierges des États-Unis", ["VN"] = "Viêt Nam", ["VU"] = "Vanuatu",
        ["WF"] = "Wallis-et-Futuna", ["WS"] = "Samoa",
        ["XK"] = "Kosovo",
        ["YE"] = "Yémen", ["YT"] = "Mayotte",
        ["ZA"] = "Afrique du Sud", ["ZM"] = "Zambie", ["ZW"] = "Zimbabwe",
    };

    // Un code qu'une base plus récente ajouterait : on montre le code tel quel plutôt qu'un nom deviné.
    private static string NomDuPays(string code) => NomsFrancais.GetValueOrDefault(code, code);

    private static int DerniereAvant6(ulong haut, ulong bas)
    {
        int gauche = 0, droite = _hauts6.Length - 1, trouve = -1;
        while (gauche <= droite)
        {
            var milieu = (gauche + droite) / 2;
            var avant = _hauts6[milieu] < haut || (_hauts6[milieu] == haut && _bas6[milieu] <= bas);
            if (avant)
            {
                trouve = milieu;
                gauche = milieu + 1;
            }
            else droite = milieu - 1;
        }
        return trouve;
    }

    /// <summary>
    /// Lit la table, une seule fois, à la première question. Une table absente ou abîmée laisse simplement les pays
    /// inconnus : ce n'est qu'un renseignement.
    /// </summary>
    private static void Charger()
    {
        if (_chargee) return;
        lock (Verrou)
        {
            if (_chargee) return;
            try
            {
                using var flux = typeof(PaysDesAdresses).Assembly.GetManifestResourceStream(Ressource);
                if (flux is null) return;
                using var lecture = new BinaryReader(new GZipStream(flux, CompressionMode.Decompress), Encoding.UTF8);
                if (Encoding.ASCII.GetString(lecture.ReadBytes(8)) != "DEPAYS1\0") return;
                var edition = lecture.ReadString();
                var codes = new string[lecture.ReadInt32()];
                for (var i = 0; i < codes.Length; i++) codes[i] = Encoding.ASCII.GetString(lecture.ReadBytes(2));

                var n4 = lecture.ReadInt32();
                var debuts4 = new uint[n4];
                for (var i = 0; i < n4; i++) debuts4[i] = lecture.ReadUInt32();
                var pays4 = new ushort[n4];
                for (var i = 0; i < n4; i++) pays4[i] = lecture.ReadUInt16();

                var n6 = lecture.ReadInt32();
                var hauts6 = new ulong[n6];
                var bas6 = new ulong[n6];
                for (var i = 0; i < n6; i++)
                {
                    hauts6[i] = lecture.ReadUInt64();
                    bas6[i] = lecture.ReadUInt64();
                }
                var pays6 = new ushort[n6];
                for (var i = 0; i < n6; i++) pays6[i] = lecture.ReadUInt16();

                (_codes, _debuts4, _pays4, _hauts6, _bas6, _pays6, Edition) = (codes, debuts4, pays4, hauts6, bas6, pays6, edition);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or OverflowException)
            {
                // Table illisible : les pays restent inconnus.
            }
            finally
            {
                _chargee = true;
            }
        }
    }
}
