using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace BBManager.Services;

// =====================================================================================
//  Limitation des essais de mot de passe (déverrouillage, confirmation)
// =====================================================================================
public static class AttemptGuard
{
    private static int _failures;
    private static DateTime _lockedUntil = DateTime.MinValue;

    /// <summary>Temps d'attente restant avant le prochain essai autorisé (zéro si libre).</summary>
    public static TimeSpan Remaining => _lockedUntil > DateTime.Now ? _lockedUntil - DateTime.Now : TimeSpan.Zero;

    public static void Success() { _failures = 0; _lockedUntil = DateTime.MinValue; }

    public static void Failure()
    {
        _failures++;
        // 4 premiers essais libres, puis 2 s, 4 s, 8 s… jusqu'à 10 min
        if (_failures >= 4)
        {
            var secs = Math.Min(600, 2 * Math.Pow(2, _failures - 4));
            _lockedUntil = DateTime.Now.AddSeconds(secs);
        }
    }
}

// =====================================================================================
//  Presse-papiers : pas d'historique Windows, pas de synchronisation cloud, effacé après 30 s
// =====================================================================================
public static class SecureClipboard
{
    private static string? _pending;
    private static DispatcherTimer? _timer;

    public static bool Copy(string text, int clearAfterSeconds = 30)
    {
        var data = new DataObject();
        data.SetText(text);
        // Formats reconnus par Windows (Win+V) et les gestionnaires de presse-papiers pour ignorer ce contenu
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));

        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                _pending = text;
                _timer?.Stop();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clearAfterSeconds) };
                _timer.Tick += (_, _) => ClearIfOurs();
                _timer.Start();
                return true;
            }
            catch { Thread.Sleep(80); }
        }
        return false;
    }

    /// <summary>Vide le presse-papiers s'il contient toujours ce que nous y avons mis.</summary>
    public static void ClearIfOurs()
    {
        _timer?.Stop();
        if (_pending == null) return;
        try { if (Clipboard.ContainsText() && Clipboard.GetText() == _pending) Clipboard.Clear(); } catch { }
        _pending = null;
    }
}

// =====================================================================================
//  Validation des entrées utilisateur (injection de paramètres dans les URL de lancement)
// =====================================================================================
public static class Validate
{
    private static readonly Regex Job = new(@"^[0-9a-fA-F\-]{8,64}$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"^[A-Za-z0-9_\-]{4,64}$", RegexOptions.Compiled);
    private static readonly Regex User = new(@"^[A-Za-z0-9_]{3,20}$", RegexOptions.Compiled);
    private static readonly Regex Flag = new(@"^[A-Za-z0-9_]{3,100}$", RegexOptions.Compiled);

    public static bool JobId(string s) => Job.IsMatch(s);
    public static bool LinkCode(string s) => Link.IsMatch(s);
    public static bool Username(string s) => User.IsMatch(s);
    public static bool FlagName(string s) => Flag.IsMatch(s);
}

// =====================================================================================
//  Durcissement du processus
// =====================================================================================
public static class ProcessHardening
{
    [DllImport("kernel32.dll")] private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll")] private static extern bool SetProcessMitigationPolicy(int policy, IntPtr buffer, UIntPtr length);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint rev, out IntPtr sd, out uint size);
    [DllImport("advapi32.dll")]
    private static extern bool GetSecurityDescriptorDacl(IntPtr sd, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;

    /// <summary>Résultat de chaque protection (pour le diagnostic), sans jamais faire échouer le démarrage.</summary>
    public static Dictionary<string, bool> Apply(bool restrictAccess = true)
    {
        var r = new Dictionary<string, bool>();

        // 1) Les DLL ne sont chargées que depuis le dossier de l'appli et System32 (pas le dossier courant ni le PATH)
        try { r["dll-search"] = SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS); } catch { r["dll-search"] = false; }

        // 2) Pas d'images distantes (partage réseau), préférence System32
        r["image-load"] = SetPolicy(10, 0x1 | 0x2 | 0x4);
        // 3) Pas de points d'extension hérités (AppInit_DLLs, hooks globaux legacy)
        r["extension-points"] = SetPolicy(6, 0x1);

        // 4) Les autres programmes de la session ne peuvent plus lire/écrire la mémoire de l'appli (où sont les cookies)
        if (restrictAccess) r["process-acl"] = RestrictProcessAccess();
        return r;
    }

    private static bool SetPolicy(int policy, uint flags)
    {
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buf, unchecked((int)flags));
            return SetProcessMitigationPolicy(policy, buf, (UIntPtr)4);
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// DACL du processus : SYSTEM et Administrateurs gardent tout. Le propriétaire (toi) garde tous les droits SAUF
    /// ceux qui permettent de voler ou d'altérer les données : lire/écrire la mémoire, créer un thread, dupliquer un handle,
    /// modifier les droits. Tu peux donc toujours voir et FERMER l'appli (Gestionnaire des tâches, taskkill).
    /// </summary>
    private static bool RestrictProcessAccess()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return false;
            // 0x13FF85 = tous les droits (0x1FFFFF) sauf CREATE_THREAD, VM_OPERATION, VM_READ, VM_WRITE, DUP_HANDLE, WRITE_DAC, WRITE_OWNER
            var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x13FF85;;;OW)(A;;0x13FF85;;;{sid})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out sd, out _)) return false;
            if (!GetSecurityDescriptorDacl(sd, out var present, out var dacl, out _) || !present) return false;
            const uint DACL = 0x4, PROTECTED_DACL = 0x80000000;
            return SetSecurityInfo(GetCurrentProcess(), 6 /* SE_KERNEL_OBJECT */, DACL | PROTECTED_DACL, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero) == 0;
        }
        catch { return false; }
        finally { if (sd != IntPtr.Zero) LocalFree(sd); }
    }

    // Masque la fenêtre dans les captures d'écran / partages d'écran (Discord, OBS…)
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    public static void SetCaptureExclusion(IntPtr hwnd, bool on)
    {
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        try { SetWindowDisplayAffinity(hwnd, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); } catch { }
    }
}

// =====================================================================================
//  Signature Authenticode : on ne lance que le vrai client Roblox
// =====================================================================================
public static class Authenticode
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Dictionary<string, (DateTime stamp, bool ok)> Cache = new();

    /// <summary>Vrai si le fichier est signé de façon valide (chaîne approuvée par Windows, fichier non modifié) par « Roblox Corporation ».</summary>
    public static bool IsSignedBy(string path, string publisher, out string detail)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var c) && c.stamp == stamp) { detail = c.ok ? "ok (cache)" : "refusé (cache)"; return c.ok; }

            bool ok = VerifyFile(path, out detail) && PublisherMatches(path, publisher, out detail);
            Cache[path] = (stamp, ok);
            return ok;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    private static bool VerifyFile(string path, out string detail)
    {
        var file = new WINTRUST_FILE_INFO { pcwszFilePath = path, cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>() };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,                 // WTD_UI_NONE
                fdwRevocationChecks = 0,        // pas de requête réseau de révocation
                dwUnionChoice = 1,              // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,              // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000,       // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int rc = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            detail = rc == 0 ? "signature valide" : $"signature invalide (0x{rc:X8})";
            return rc == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    private static bool PublisherMatches(string path, string publisher, out string detail)
    {
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        bool ok = cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase);
        detail = ok ? $"signé par {publisher}" : $"éditeur inattendu : {cert.Subject}";
        return ok;
    }
}
