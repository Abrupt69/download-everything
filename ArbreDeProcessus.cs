using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YtDlpInterface;

/// <summary>
/// Arrêt propre de yt-dlp. Son exécutable est fait de deux étages : un lanceur, qui déballe yt-dlp dans un
/// dossier temporaire d'une trentaine de Mo, et le vrai travailleur, qui lance à son tour ffmpeg.
/// Tuer tout d'un coup laisserait ce dossier temporaire derrière soi à chaque annulation.
/// On arrête donc d'abord les descendants, puis on laisse au lanceur le temps de faire son ménage.
/// </summary>
internal static partial class ArbreDeProcessus
{
    public static void Arreter(Process racine, int delaiDeMenageMs = 3000)
    {
        try
        {
            if (racine.HasExited) return;

#if PORTABLE
            if (!OperatingSystem.IsWindows())
            {
                // Sous Linux : le signal d'arrêt ordinaire d'abord, que le lanceur transmet à yt-dlp et qui lui laisse
                // faire son ménage, puis l'arrêt ferme de tout l'arbre s'il tarde.
                _ = kill(racine.Id, 15);
                if (!racine.WaitForExit(delaiDeMenageMs)) racine.Kill(entireProcessTree: true);
                return;
            }
#endif

            foreach (var identifiant in Descendants(racine.Id))
            {
                try
                {
                    using var descendant = Process.GetProcessById(identifiant);
                    descendant.Kill();
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Déjà terminé, ou hors de portée. Le filet ci-dessous s'en charge au besoin.
                }
            }

            // Filet. Si le lanceur ne s'arrête pas de lui-même, tout l'arbre est tué.
            if (!racine.WaitForExit(delaiDeMenageMs)) racine.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // Le processus s'est terminé entre-temps, il n'y a plus rien à arrêter.
        }
    }

#if PORTABLE
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processus, int signal);
#endif

    /// <summary>Tous les descendants d'un processus, enfants d'abord, lus dans un instantané du système.</summary>
    private static List<int> Descendants(int racine)
    {
        var enfantsDe = new Dictionary<uint, List<uint>>();
        var instantane = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (instantane == InvalidHandle) return [];
        try
        {
            var entree = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var suite = Process32FirstW(instantane, ref entree); suite; suite = Process32NextW(instantane, ref entree))
            {
                if (!enfantsDe.TryGetValue(entree.th32ParentProcessID, out var liste))
                    enfantsDe[entree.th32ParentProcessID] = liste = [];
                liste.Add(entree.th32ProcessID);
            }
        }
        finally
        {
            CloseHandle(instantane);
        }

        var trouves = new List<int>();
        var aVisiter = new Queue<uint>([(uint)racine]);
        var vus = new HashSet<uint> { (uint)racine };
        while (aVisiter.Count > 0)
        {
            if (!enfantsDe.TryGetValue(aVisiter.Dequeue(), out var enfants)) continue;
            // « vus » protège d'une boucle si Windows a réattribué un ancien numéro de processus.
            foreach (var enfant in enfants.Where(vus.Add))
            {
                trouves.Add((int)enfant);
                aVisiter.Enqueue(enfant);
            }
        }
        return trouves;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint drapeaux, uint processus);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr instantane, ref PROCESSENTRY32W entree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr instantane, ref PROCESSENTRY32W entree);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr poignee);
}
