using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BBManager.Services;

public record LogIdentity(long UserId, long PlaceId);

/// <summary>
/// Retrouve le compte d'un client Roblox déjà ouvert grâce à son journal (%LOCALAPPDATA%\Roblox\logs) :
/// le nom du fichier contient l'heure de démarrage du client, et le contenu l'identifiant du compte
/// (« userid:… » à chaque connexion à un jeu, et le ticket « "UserId":… »).
/// </summary>
public static class RobloxLogs
{
    private static string Dir => Path.Combine(RobloxClient.RobloxDataDir, "logs");

    private static readonly Regex NameRx = new(@"_(\d{8}T\d{6}Z)_Player_[0-9A-Za-z]+_last\.log$", RegexOptions.Compiled);
    private static readonly Regex UserRx = new(@"userid:(\d{5,})|""UserId""%3a(\d{5,})|""UserId"":(\d{5,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaceRx = new(@"game_join_loadtime: placeid:(\d+)", RegexOptions.Compiled);

    /// <summary>Journal du client démarré à cette heure (UTC) : le plus proche dans une fenêtre de -5 s à +40 s.</summary>
    public static FileInfo? FindLog(DateTime processStartUtc)
    {
        try
        {
            if (!Directory.Exists(Dir)) return null;
            FileInfo? best = null;
            double bestAbs = double.MaxValue;
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("*_Player_*_last.log"))
            {
                if (f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
                var m = NameRx.Match(f.Name);
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
                var delta = (ts - processStartUtc).TotalSeconds;
                if (delta < -5 || delta > 40) continue;
                if (Math.Abs(delta) < bestAbs) { bestAbs = Math.Abs(delta); best = f; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Compte (et dernier jeu) lus dans le journal ; null tant que le client n'a pas rejoint de jeu.</summary>
    public static LogIdentity? ParseIdentity(FileInfo log)
    {
        try
        {
            using var fs = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Les identifiants apparaissent dans les premières minutes : inutile de tout lire sur un journal de plusieurs Mo
            var buf = new byte[Math.Min(fs.Length, 1_500_000)];
            int read = fs.Read(buf, 0, buf.Length);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            var um = UserRx.Match(text);
            if (!um.Success) return null;
            var idStr = um.Groups[1].Success ? um.Groups[1].Value : um.Groups[2].Success ? um.Groups[2].Value : um.Groups[3].Value;

            long place = 0;
            var pm = PlaceRx.Matches(text);
            if (pm.Count > 0) long.TryParse(pm[^1].Groups[1].Value, out place);
            return new LogIdentity(long.Parse(idStr), place);
        }
        catch { return null; }
    }
}
