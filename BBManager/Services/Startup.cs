using Microsoft.Win32;

namespace BBManager.Services;

/// <summary>Démarrage avec Windows (clé « Run » de l'utilisateur, aucun droit administrateur requis).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KBAccountManager";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
    }

    /// <summary>Si le démarrage automatique est actif, remet à jour le chemin (utile quand l'exe a été déplacé).</summary>
    public static void RefreshPath()
    {
        try { if (IsEnabled()) Set(true); } catch { }
    }
}
