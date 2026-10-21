using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using BBManager.Core;
using Microsoft.Win32;

namespace BBManager.Services;

public record UpdateInfo(Version Version, string Url, string Sha256, string Signature, string Notes)
{
    public string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";
}

/// <summary>
/// Mises à jour : l'appli lit un petit fichier JSON (« flux ») publié par toi, vérifie sa signature, télécharge l'installateur,
/// vérifie son empreinte SHA-256, puis le lance. Rien n'est installé si la signature ne correspond pas à TA clé :
/// même si le site d'hébergement était piraté, personne ne pourrait faire installer autre chose.
///
/// Flux : { "version": "1.2.0", "url": "https://…/KB-Account-Manager-Setup.exe", "sha256": "…", "signature": "…", "notes": "…" }
/// Signature (ECDSA P-256 / SHA-256, DER, base64) de la chaîne :  KBM-UPDATE|version|sha256|url
/// </summary>
public class UpdateService : ObservableObject
{
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    private static string? Resource(string name)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    /// <summary>Adresse du flux : intégrée à l'appli au moment de la compilation (KBM_UPDATE_FEED permet de tester).</summary>
    public static string? FeedUrl =>
        Environment.GetEnvironmentVariable("KBM_UPDATE_FEED") is { Length: > 0 } env ? env
        : Resource("update-feed.txt") is { Length: > 0 } f ? f : null;

    /// <summary>Liste des changements de CETTE version, écrite à la publication (release.py) et embarquée dans l'appli.</summary>
    public static string WhatsNew => Resource("whatsnew.txt") is { Length: > 0 } w ? w : "- Corrections et améliorations.";

    private static string? PublicKey => Resource("update-pubkey.txt") is { Length: > 0 } k ? k : null;

    public bool IsConfigured => FeedUrl != null && PublicKey != null;

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
    { Timeout = TimeSpan.FromMinutes(3) };

    private UpdateInfo? _available;
    private string _status = "";
    private bool _busy;
    private DateTime? _lastCheck;

    public UpdateInfo? Available { get => _available; private set { if (Set(ref _available, value)) { Raise(nameof(HasUpdate)); Raise(nameof(BadgeText)); } } }
    public bool HasUpdate => _available != null;
    public string BadgeText => _available == null ? "" : $"Version {_available.VersionText} disponible";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public DateTime? LastCheck { get => _lastCheck; private set { if (Set(ref _lastCheck, value)) Raise(nameof(LastCheckText)); } }
    public string LastCheckText => _lastCheck == null ? "jamais" : _lastCheck.Value.ToString("dd/MM HH:mm");

    /// <summary>Appelée (une fois par version) quand une mise à jour vérifiée est trouvée.</summary>
    public event Action<UpdateInfo>? UpdateFound;
    private Version? _announced;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(6) };

    /// <summary>Vérifie peu après le démarrage, puis toutes les 6 heures.</summary>
    public void StartAutoCheck(Func<bool> enabled)
    {
        if (!IsConfigured) { Status = "Aucune adresse de mise à jour n'est configurée dans cette version."; return; }
        _timer.Tick += async (_, _) => { if (enabled()) await CheckAsync(); };
        _timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += async (_, _) => { first.Stop(); if (enabled()) await CheckAsync(); };
        first.Start();
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsConfigured) { Status = "Aucune adresse de mise à jour n'est configurée dans cette version."; return null; }
        if (Busy) return Available;
        Busy = true;
        Status = "Vérification…";
        try
        {
            var feed = FeedUrl!;
            if (!AllowedUrl(feed)) throw new InvalidOperationException("adresse de flux non autorisée");
            using var resp = await _http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var json = await ReadLimited(resp.Content, 64 * 1024);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var info = new UpdateInfo(
                Version.Parse(root.GetProperty("version").GetString()!),
                root.GetProperty("url").GetString()!,
                root.GetProperty("sha256").GetString()!.ToLowerInvariant(),
                root.GetProperty("signature").GetString()!,
                root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "");

            if (!AllowedUrl(info.Url)) throw new InvalidOperationException("adresse de téléchargement non autorisée");
            if (info.Sha256.Length != 64 || !info.Sha256.All(Uri.IsHexDigit)) throw new InvalidOperationException("empreinte invalide");
            if (!VerifySignature(info))
            {
                Available = null;
                Status = "Mise à jour ignorée : signature invalide.";
                return null;
            }

            LastCheck = DateTime.Now;
            if (info.Version <= Current)
            {
                Available = null;
                Status = $"Tu es à jour (version {CurrentText}).";
                return null;
            }

            Available = info;
            Status = $"La version {info.VersionText} est disponible.";
            if (_announced != info.Version) { _announced = info.Version; UpdateFound?.Invoke(info); }
            return info;
        }
        catch (Exception ex)
        {
            Status = "Vérification impossible : " + ex.Message;
            return null;
        }
        finally { Busy = false; }
    }

    /// <summary>HTTPS uniquement (http n'est toléré que vers cette machine, pour les tests). La signature protège de toute façon le contenu.</summary>
    private static bool AllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost");
    }

    private static async Task<string> ReadLimited(HttpContent content, int max)
    {
        await using var s = await content.ReadAsStreamAsync();
        var buf = new byte[max + 1];
        int total = 0, n;
        while ((n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total))) > 0 && total < buf.Length) total += n;
        if (total > max) throw new InvalidOperationException("réponse trop volumineuse");
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    public static bool VerifySignature(UpdateInfo i)
    {
        try
        {
            var pub = PublicKey;
            if (pub == null) return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
            var msg = Encoding.UTF8.GetBytes($"KBM-UPDATE|{i.Version}|{i.Sha256}|{i.Url}");
            return ecdsa.VerifyData(msg, Convert.FromBase64String(i.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }

    /// <summary>Télécharge l'installateur, vérifie son empreinte, et retourne son chemin.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (!VerifySignature(info)) throw new InvalidOperationException("Signature de la mise à jour invalide.");

        var dir = Path.Combine(Path.GetTempPath(), "KBManager-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"KB-Account-Manager-Setup-{info.VersionText}.exe");
        if (File.Exists(path)) File.Delete(path);

        using var resp = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        if (total > 200_000_000) throw new InvalidOperationException("Fichier trop volumineux.");

        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                done += n;
                if (done > 200_000_000) throw new InvalidOperationException("Fichier trop volumineux.");
                sha.TransformBlock(buf, 0, n, null, 0);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (hash != info.Sha256)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("L'empreinte du fichier téléchargé ne correspond pas : mise à jour refusée.");
        }
        return path;
    }

    /// <summary>
    /// Lance l'installateur puis quitte l'appli. Si l'appli a été installée par l'installateur, la mise à jour se fait en silence
    /// dans le même dossier et l'appli se relance toute seule ; sinon l'installateur s'ouvre normalement.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string? installed = null;
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KBAccountManager"); installed = (k?.GetValue("InstallLocation") as string)?.TrimEnd('\\', '/'); }
        catch { }

        bool silent = installed != null && string.Equals(installed, baseDir, StringComparison.OrdinalIgnoreCase);
        var args = silent ? $"/S /launch /showerrors /waitpid={Environment.ProcessId} \"/D={baseDir}\"" : "";
        Process.Start(new ProcessStartInfo(installerPath, args) { UseShellExecute = false });
    }
}
