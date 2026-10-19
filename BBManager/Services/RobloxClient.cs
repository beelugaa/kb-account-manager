using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BBManager.Models;
using Microsoft.Win32;

namespace BBManager.Services;

/// <summary>Localisation du client Roblox + écriture des réglages (FPS, FFlags, plein écran).</summary>
public static class RobloxClient
{
    public static string? FindPlayerExe(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.RobloxExePath) && File.Exists(s.RobloxExePath)) return s.RobloxExePath;

        // 1) le handler de protocole roblox-player (pointe vers la version actuelle)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command");
            var cmd = key?.GetValue(null) as string;
            if (cmd != null)
            {
                var m = Regex.Match(cmd, "\"([^\"]+RobloxPlayerBeta\\.exe)\"", RegexOptions.IgnoreCase);
                if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
            }
        }
        catch { }

        // 2) la version la plus récente dans %LOCALAPPDATA%\Roblox\Versions
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
            if (Directory.Exists(dir))
                return Directory.GetDirectories(dir)
                    .Select(d => Path.Combine(d, "RobloxPlayerBeta.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
        }
        catch { }
        return null;
    }

    public static string RobloxDataDir =>
        Environment.GetEnvironmentVariable("KBM_ROBLOX_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

    // ---- FFlags ------------------------------------------------------------
    public static Dictionary<string, object> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, object>();
        if (s.UnlockFps) flags["DFIntTaskSchedulerTargetFps"] = (long)(s.FpsCap <= 0 ? 9999 : s.FpsCap);
        if (s.LowGraphics)
        {
            flags["DFIntDebugFRMQualityLevelOverride"] = 1L;
            // Textures au plus bas : c'est ce qui réduit le plus la RAM / VRAM par instance
            flags["DFFlagTextureQualityOverrideEnabled"] = true;
            flags["DFIntTextureQualityOverride"] = 0L;
        }
        foreach (var f in s.CustomFlags.Where(f => f.Enabled && Validate.FlagName(f.Key.Trim())))
            flags[f.Key.Trim()] = ParseValue(f.Value);
        return flags;
    }

    private static object ParseValue(string v)
    {
        v = v.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(v, out var l)) return l;
        return v;
    }

    /// <summary>Écrit ClientAppSettings.json dans la version du client. Ne touche qu'aux clés gérées par l'appli.</summary>
    public static string ApplyFlags(AppSettings s, string playerExe)
    {
        var dir = Path.Combine(Path.GetDirectoryName(playerExe)!, "ClientSettings");
        var file = Path.Combine(dir, "ClientAppSettings.json");

        JsonObject obj;
        try { obj = File.Exists(file) ? (JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject()) : new JsonObject(); }
        catch { obj = new JsonObject(); }

        foreach (var k in s.AppliedFlagKeys) obj.Remove(k);

        var flags = BuildFlags(s);
        foreach (var (k, v) in flags)
        {
            obj[k] = v switch
            {
                bool b => JsonValue.Create(b),
                long l => JsonValue.Create(l),
                _ => JsonValue.Create(v.ToString()),
            };
        }
        s.AppliedFlagKeys = flags.Keys.ToList();

        if (obj.Count == 0)
        {
            if (File.Exists(file)) File.Delete(file);
        }
        else
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return file;
    }

    // ---- Mode fenêtré ------------------------------------------------------
    public static void ForceWindowed()
    {
        try
        {
            var f = Path.Combine(RobloxDataDir, "GlobalBasicSettings_13.xml");
            if (!File.Exists(f)) return;
            var txt = File.ReadAllText(f);
            var fixedTxt = Regex.Replace(txt, "(<bool name=\"Fullscreen\">)true(</bool>)", "$1false$2");
            if (fixedTxt != txt) File.WriteAllText(f, fixedTxt);
        }
        catch { }
    }

    public static IEnumerable<Process> ListPlayers()
        => Process.GetProcessesByName("RobloxPlayerBeta");

    public static int KillAll()
    {
        int n = 0;
        foreach (var p in ListPlayers())
        {
            try { p.Kill(true); n++; } catch { }
            p.Dispose();
        }
        return n;
    }
}
