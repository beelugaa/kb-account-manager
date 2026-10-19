using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace KBSetup;

public record InstallOptions(string Dir, bool Desktop, bool StartMenu, bool AutoStart, bool Launch);

/// <summary>Installation par utilisateur : dossier dans %LOCALAPPDATA%\Programs, raccourcis, entrée « Applications installées ».</summary>
public static class InstallerCore
{
    public const string AppName = "KB Account Manager";
    public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KBAccountManager";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ExeName = "KBManager.exe";

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    /// <summary>Dossier d'une installation existante (pour proposer « Mettre à jour »).</summary>
    public static string? ExistingDir()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegKey);
            var d = k?.GetValue("InstallLocation") as string;
            return !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, ExeName)) ? d : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Vrai si l'appli installée dans ce dossier tourne (son exécutable est verrouillé). On ne regarde pas le nom du processus :
    /// une autre copie de KB Manager, ailleurs, ne doit pas bloquer cette installation.
    /// </summary>
    public static bool IsAppRunning(string dir)
    {
        try
        {
            var exe = Path.Combine(Path.GetFullPath(dir.Trim().Trim('"')), ExeName);
            if (!File.Exists(exe)) return false;
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    /// <summary>Refuse les destinations dangereuses : racine d'un disque, dossier Windows, profil utilisateur…</summary>
    public static string ValidateDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("Choisis un dossier d'installation.");
        var full = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\', '/');
        if (!Path.IsPathRooted(full)) throw new InvalidOperationException("Le dossier doit être un chemin complet.");
        if (Path.GetPathRoot(full)!.TrimEnd('\\') == full) throw new InvalidOperationException("Choisis un sous-dossier, pas la racine d'un disque.");

        string[] forbidden =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        if (forbidden.Any(f => string.Equals(f.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Ce dossier est un dossier système ou personnel : choisis-en un dédié.");

        // Un dossier existant et non vide qui n'est pas une ancienne installation reçoit un sous-dossier :
        // on ne supprime jamais les fichiers de quelqu'un d'autre.
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, ExeName)))
            full = Path.Combine(full, AppName);
        return full;
    }

    public static void Install(InstallOptions o, Action<int, string> progress)
    {
        var dir = ValidateDir(o.Dir);
        progress(2, "Préparation du dossier…");
        Directory.CreateDirectory(dir);

        // Mise à jour : on retire les anciens fichiers de l'appli (jamais les données, qui sont dans %APPDATA%)
        if (File.Exists(Path.Combine(dir, ExeName)))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); }
                catch (IOException) { throw new IOException("Des fichiers sont utilisés par KB Manager. Quitte l'appli (icône de la barre système > Quitter) puis réessaie."); }
            }
        }

        // Extraction (avec protection contre les chemins « ../ » dans l'archive)
        progress(5, "Copie des fichiers…");
        var asm = Assembly.GetExecutingAssembly();
        using (var payload = asm.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("Archive d'installation absente."))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var root = dir + Path.DirectorySeparatorChar;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive invalide.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
                progress(5 + (int)(80.0 * (i + 1) / entries.Count), $"Copie des fichiers… ({i + 1}/{entries.Count})");
            }
        }

        // Désinstalleur
        progress(88, "Création du désinstalleur…");
        using (var s = asm.GetManifestResourceStream("Uninstall.ps1")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            File.WriteAllText(Path.Combine(dir, "Uninstall.ps1"), r.ReadToEnd(), new UTF8Encoding(true));

        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe)) throw new FileNotFoundException("L'installation est incomplète : " + ExeName + " est absent.");

        // Raccourcis
        progress(92, "Raccourcis…");
        var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), AppName + ".lnk");
        var menuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        SetShortcut(desktopLnk, exe, dir, o.Desktop);
        SetShortcut(menuLnk, exe, dir, o.StartMenu);

        // Démarrage avec Windows
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (o.AutoStart) run.SetValue("KBAccountManager", $"\"{exe}\" --tray");
            else run.DeleteValue("KBAccountManager", false);
        }

        // Entrée « Applications installées »
        progress(96, "Enregistrement dans Windows…");
        long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "KB");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("UninstallString", $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(dir, "Uninstall.ps1")}\"");
        }
        progress(100, "Terminé.");
    }

    private static void SetShortcut(string lnk, string target, string workDir, bool create)
    {
        try
        {
            if (!create) { if (File.Exists(lnk)) File.Delete(lnk); return; }
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic s = sh.CreateShortcut(lnk);
            s.TargetPath = target;
            s.WorkingDirectory = workDir;
            s.IconLocation = target + ",0";
            s.Description = AppName;
            s.Save();
        }
        catch { /* un raccourci manquant ne doit pas faire échouer l'installation */ }
    }
}
