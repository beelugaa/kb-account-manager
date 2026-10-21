using System.Text.Json.Serialization;
using BBManager.Core;
using BBManager.Services;

namespace BBManager.Models;

public enum JoinMode { Place, JobId, PrivateServer, FollowUser, Home }
public enum RamMode { Trim, HardWorkingSet, JobObject }
public enum CpuPriority { Normal, BelowNormal, Idle, AboveNormal }
public enum AfkMethod { Jump, Discreet }

public class Account : ObservableObject
{
    // ---- Données persistées -------------------------------------------------
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Cookie { get; set; } = "";
    public Guid? GroupId { get; set; }
    private int _sortOrder; public int SortOrder { get => _sortOrder; set => Set(ref _sortOrder, value); }
    private DateTime? _lastUsed; public DateTime? LastUsed { get => _lastUsed; set { if (Set(ref _lastUsed, value)) Raise(nameof(LastUsedText)); } }
    private DateTime _addedAt = DateTime.Now; public DateTime AddedAt { get => _addedAt; set => Set(ref _addedAt, value); }
    public long PlaceId { get; set; }                // jeu par défaut propre au compte (0 = global)
    private int _ramLimitMb; public int RamLimitMb { get => _ramLimitMb; set => Set(ref _ramLimitMb, value); }  // 0 = limite globale
    public bool KeepAlive { get; set; }              // relance auto si la fenêtre se ferme
    private bool _antiAfk = true;                    // participe à l'anti-AFK global
    public bool AntiAfk { get => _antiAfk; set { if (Set(ref _antiAfk, value)) Raise(nameof(AfkConfigText)); } }
    public int AfkIntervalSec { get; set; }          // 0 = intervalle par défaut
    public AfkMethod? AfkMethodOverride { get; set; } // null = méthode par défaut
    public string AvatarUrl { get; set; } = "";

    private long _robux; public long Robux { get => _robux; set { if (Set(ref _robux, value)) Raise(nameof(RobuxText)); } }
    private bool _valid = true; public bool CookieValid { get => _valid; set { if (Set(ref _valid, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusKind)); } } }

    // ---- Runtime (non persisté) --------------------------------------------
    private string _groupName = "";
    private string _groupColor = "#7C5CFF";
    private int _groupSort;
    private bool _isRunning;
    private int _pid;
    private long _ramBytes;
    private DateTime? _startedAt;
    private bool _isBusy;
    private string _busyText = "";

    [JsonIgnore] public string GroupName { get => _groupName; set => Set(ref _groupName, value); }
    [JsonIgnore] public string GroupColor { get => _groupColor; set => Set(ref _groupColor, value); }
    [JsonIgnore] public int GroupSort { get => _groupSort; set => Set(ref _groupSort, value); }

    [JsonIgnore] public bool IsRunning
    {
        get => _isRunning;
        set { if (Set(ref _isRunning, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusKind)); Raise(nameof(RunSort)); } }
    }
    [JsonIgnore] public int Pid { get => _pid; set { if (Set(ref _pid, value)) Raise(nameof(StatusText)); } }
    [JsonIgnore] public long RamBytes { get => _ramBytes; set { if (Set(ref _ramBytes, value)) Raise(nameof(RamText)); } }
    private double _ramRatio;
    [JsonIgnore] public double RamRatio { get => _ramRatio; set => Set(ref _ramRatio, value); }
    [JsonIgnore] public DateTime? StartedAt { get => _startedAt; set { if (Set(ref _startedAt, value)) Raise(nameof(UptimeText)); } }
    [JsonIgnore] public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusKind)); } } }
    [JsonIgnore] public string BusyText { get => _busyText; set { if (Set(ref _busyText, value)) Raise(nameof(StatusText)); } }

    [JsonIgnore] public IntPtr JobHandle { get; set; }
    [JsonIgnore] public DateTime? ProcStartUtc { get; set; }
    [JsonIgnore] public int ProfileRamLimitMb { get; set; }   // limite imposée par le profil qui a lancé ce compte (0 = aucune)
    [JsonIgnore] public DateTime? NextAfkAt { get; set; }
    [JsonIgnore] public DateTime? LastAfkAt { get; set; }
    [JsonIgnore] public bool AfkQueued { get; set; }
    [JsonIgnore] public string AfkConfigText
    {
        get
        {
            var s = AppState.Current?.Settings;
            if (s == null) return "";
            if (!AntiAfk) return "Désactivé pour ce compte";
            var m = AfkMethodOverride ?? s.AfkMethod;
            var iv = AfkIntervalSec > 0 ? AfkIntervalSec : s.AfkIntervalSec;
            bool custom = AfkIntervalSec > 0 || AfkMethodOverride != null;
            return $"{(m == AfkMethod.Discreet ? "Discret" : "Saut")} · toutes les {(iv >= 120 ? $"{iv / 60.0:0.#} min" : $"{iv} s")}{(custom ? " · perso" : "")}";
        }
    }
    public void RefreshAfkConfig() => Raise(nameof(AfkConfigText));
    private string _afkText = "";
    [JsonIgnore] public string AfkText { get => _afkText; set => Set(ref _afkText, value); }
    private long _commitBytes;
    [JsonIgnore] public long CommitBytes { get => _commitBytes; set { if (Set(ref _commitBytes, value)) Raise(nameof(CommitText)); } }
    [JsonIgnore] public string CommitText => IsRunning && CommitBytes > 0 ? $"engagée {Util.FormatBytes(CommitBytes)}" : "";
    [JsonIgnore] public bool HardWsApplied { get; set; }
    [JsonIgnore] public bool LimitWarned { get; set; }
    [JsonIgnore] public bool UserClosed { get; set; }
    [JsonIgnore] public DateTime? RelaunchAt { get; set; }
    [JsonIgnore] public LaunchRequest? LastLaunch { get; set; }

    [JsonIgnore] public string SortKey => Title.ToLowerInvariant();
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Alias) ? Username : Alias;
    [JsonIgnore] public string SubTitle => string.IsNullOrWhiteSpace(Alias) ? DisplayName : $"@{Username}";
    [JsonIgnore] public string RobuxText => Robux > 0 ? $"{Robux:N0} R$" : "—";
    [JsonIgnore] public string RamText => IsRunning ? Util.FormatBytes(RamBytes) : "";
    [JsonIgnore] public string UptimeText => IsRunning && StartedAt != null ? Util.FormatSpan(DateTime.Now - StartedAt.Value) : "";
    [JsonIgnore] public int RunSort => IsRunning ? 0 : 1;
    [JsonIgnore] public string LastUsedText => LastUsed == null ? "jamais" : LastUsed.Value.ToString("dd/MM HH:mm");

    // 0 = hors ligne, 1 = en jeu, 2 = cookie invalide, 3 = occupé
    [JsonIgnore] public int StatusKind => IsBusy ? 3 : !CookieValid ? 2 : IsRunning ? 1 : 0;
    [JsonIgnore] public string StatusText =>
        IsBusy ? BusyText : !CookieValid ? "Cookie invalide" : IsRunning ? $"En jeu · PID {Pid}" : "Hors ligne";

    public void TickUptime() => Raise(nameof(UptimeText));

    public void RefreshIdentity()
    {
        Raise(nameof(Title)); Raise(nameof(SortKey)); Raise(nameof(SubTitle)); Raise(nameof(Username)); Raise(nameof(DisplayName));
        Raise(nameof(AvatarUrl)); Raise(nameof(LastUsedText)); Raise(nameof(Notes)); Raise(nameof(Alias));
        Raise(nameof(KeepAlive)); Raise(nameof(RamLimitMb)); Raise(nameof(PlaceId));
    }
}

public class AccountGroup : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "Groupe"; public string Name { get => _name; set => Set(ref _name, value); }
    private string _color = "#7C5CFF"; public string Color { get => _color; set => Set(ref _color, value); }
    public int SortOrder { get; set; }

    public override string ToString() => Name;
    private int _count; [JsonIgnore] public int Count { get => _count; set => Set(ref _count, value); }
    private bool _selected; [JsonIgnore] public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public class SavedPlace : ObservableObject
{
    public long PlaceId { get; set; }
    private string _name = ""; public string Name { get => _name; set { Set(ref _name, value); Raise(nameof(Label)); } }
    private string _iconUrl = ""; public string IconUrl { get => _iconUrl; set { Set(ref _iconUrl, value); Raise(nameof(HasIcon)); } }
    [JsonIgnore] public bool HasIcon => IconUrl.Length > 0;
    [JsonIgnore] public string ShortName => string.IsNullOrWhiteSpace(Name) ? $"Place {PlaceId}" : Name;
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) ? PlaceId.ToString() : $"{Name}  ({PlaceId})";
    public override string ToString() => Label;
}

public class FlagEntry : ObservableObject
{
    private string _key = ""; public string Key { get => _key; set => Set(ref _key, value); }
    private string _value = ""; public string Value { get => _value; set => Set(ref _value, value); }
    private bool _enabled = true; public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

/// <summary>Instance en cours au moment de la sauvegarde : permet de la « reprendre » après un redémarrage de l'appli.</summary>
public static class Clamp
{
    public static int Int(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
    public static string Str(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];
    public static string Color(string? c, string fallback)
        => c != null && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$") ? c : fallback;
}

/// <summary>Profil de lancement : un groupe de comptes + un jeu + des options, relançables en un clic ou à heure fixe.</summary>
public class LaunchProfile : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public List<Guid> AccountIds { get; set; } = new();
    public JoinMode Mode { get; set; } = JoinMode.Place;
    public long PlaceId { get; set; }
    public string Value { get; set; } = "";
    public int RamLimitMb { get; set; }                  // 0 = réglage habituel de chaque compte
    public bool TileAfter { get; set; }                  // ranger les fenêtres après le lancement
    public bool ScheduleEnabled { get; set; }
    public string ScheduleTime { get; set; } = "18:00";  // HH:mm
    public int ScheduleDays { get; set; }                // bit 0 = lundi … bit 6 = dimanche ; 0 = tous les jours
    public DateTime? LastRun { get; set; }

    // Textes affichés (calculés par AppState.RefreshProfiles)
    private string _accountsText = "", _gameText = "", _scheduleText = "";
    [JsonIgnore] public string AccountsText { get => _accountsText; set => Set(ref _accountsText, value); }
    [JsonIgnore] public string GameText { get => _gameText; set => Set(ref _gameText, value); }
    [JsonIgnore] public string ScheduleText { get => _scheduleText; set => Set(ref _scheduleText, value); }
    private bool _running;
    [JsonIgnore] public bool IsRunning { get => _running; set => Set(ref _running, value); }
}

public class SessionRecord
{
    public Guid AccountId { get; set; }
    public int Pid { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTime StartedAt { get; set; }
    public JoinMode Mode { get; set; }
    public long PlaceId { get; set; }
    public string Value { get; set; } = "";
}

public class DataFile
{
    public List<SessionRecord> Sessions { get; set; } = new();
    public List<LaunchProfile> Profiles { get; set; } = new();

    /// <summary>Un fichier de données modifié ou corrompu ne doit jamais provoquer de valeurs absurdes (plafonds, couleurs, tailles).</summary>
    public void Sanitize()
    {
        if (Accounts.Count > 5000) Accounts = Accounts.Take(5000).ToList();
        if (Groups.Count > 500) Groups = Groups.Take(500).ToList();
        if (Places.Count > 1000) Places = Places.Take(1000).ToList();
        if (Sessions.Count > 5000) Sessions = Sessions.Take(5000).ToList();
        if (Profiles.Count > 200) Profiles = Profiles.Take(200).ToList();
        foreach (var p in Profiles)
        {
            p.Name = Clamp.Str(p.Name, 60);
            if (p.AccountIds.Count > 1000) p.AccountIds = p.AccountIds.Take(1000).ToList();
            p.RamLimitMb = Clamp.Int(p.RamLimitMb, 0, 131072);
            p.ScheduleDays = Clamp.Int(p.ScheduleDays, 0, 127);
            if (!System.Text.RegularExpressions.Regex.IsMatch(p.ScheduleTime ?? "", @"^([01]\d|2[0-3]):[0-5]\d$")) p.ScheduleTime = "18:00";
            p.Value = Clamp.Str(p.Value, 300);
            if (p.PlaceId < 0) p.PlaceId = 0;
        }
        foreach (var a in Accounts)
        {
            a.Username = Clamp.Str(a.Username, 64); a.DisplayName = Clamp.Str(a.DisplayName, 128);
            a.Alias = Clamp.Str(a.Alias, 100); a.Notes = Clamp.Str(a.Notes, 4000);
            a.Cookie = Clamp.Str(a.Cookie, 4096); a.AvatarUrl = a.AvatarUrl.StartsWith("https://") ? Clamp.Str(a.AvatarUrl, 512) : "";
            a.RamLimitMb = Clamp.Int(a.RamLimitMb, 0, 131072);
            a.AfkIntervalSec = a.AfkIntervalSec == 0 ? 0 : Clamp.Int(a.AfkIntervalSec, 15, 3600);
            if (a.PlaceId < 0) a.PlaceId = 0;
        }
        foreach (var g in Groups) { g.Name = Clamp.Str(g.Name, 60); g.Color = Clamp.Color(g.Color, "#7C5CFF"); }
        foreach (var p in Places)
        {
            p.Name = Clamp.Str(p.Name, 120);
            p.IconUrl = BBManager.Services.RobloxCdn.IsImageUrl(p.IconUrl) ? p.IconUrl : "";   // le fichier est modifiable : on n'affiche que des images du CDN de Roblox
        }
    }
    public List<Account> Accounts { get; set; } = new();
    public List<AccountGroup> Groups { get; set; } = new();
    public List<SavedPlace> Places { get; set; } = new();
}

public class AppSettings : ObservableObject
{
    /// <summary>Borne toutes les valeurs : settings.json est en clair, donc modifiable par n'importe quel programme de la session.</summary>
    public void Sanitize()
    {
        DefaultRamLimitMb = Clamp.Int(DefaultRamLimitMb, 0, 131072);
        FpsCap = Clamp.Int(FpsCap, 0, 1000);
        RamCheckSeconds = Clamp.Int(RamCheckSeconds, 1, 60);
        CoresPerInstance = Clamp.Int(CoresPerInstance, 0, 256);
        LaunchDelaySec = Clamp.Int(LaunchDelaySec, 0, 600);
        TileMonitor = Clamp.Int(TileMonitor, 0, 16);
        TileColumns = Clamp.Int(TileColumns, 0, 20);
        TileDelaySec = Clamp.Int(TileDelaySec, 0, 120);
        AutoRejoinDelaySec = Clamp.Int(AutoRejoinDelaySec, 1, 600);
        AfkIntervalSec = Clamp.Int(AfkIntervalSec, 15, 3600);
        AfkGapSec = Clamp.Int(AfkGapSec, 5, 600);
        AfkIdleGateSec = Clamp.Int(AfkIdleGateSec, 2, 600);
        AutoLockMinutes = Clamp.Int(AutoLockMinutes, 0, 1440);
        Accent = Clamp.Color(Accent, "#18B8FF");
        LastJoinValue = Clamp.Str(LastJoinValue, 300);
        LastRunVersion = Clamp.Str(LastRunVersion, 32);

        // Le chemin du client n'est accepté que s'il désigne bien RobloxPlayerBeta.exe (sinon un fichier settings.json
        // modifié pourrait faire exécuter n'importe quel programme au prochain lancement)
        if (!string.IsNullOrWhiteSpace(RobloxExePath) &&
            !string.Equals(System.IO.Path.GetFileName(RobloxExePath), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
            RobloxExePath = "";

        if (CustomFlags.Count > 200) while (CustomFlags.Count > 200) CustomFlags.RemoveAt(CustomFlags.Count - 1);
        foreach (var f in CustomFlags.ToList())
        {
            f.Key = Clamp.Str(f.Key, 100); f.Value = Clamp.Str(f.Value, 200);
            if (f.Key.Length > 0 && !BBManager.Services.Validate.FlagName(f.Key)) f.Enabled = false;
        }
        if (AppliedFlagKeys.Count > 400) AppliedFlagKeys = AppliedFlagKeys.Take(400).ToList();
    }

    // Multi-instance
    public bool MultiInstance { get; set; } = true;

    // FPS / client
    public bool UnlockFps { get; set; } = false;
    private int _fpsCap = 240; public int FpsCap { get => _fpsCap; set => Set(ref _fpsCap, value); }  // 0 = illimité
    public bool ForceWindowed { get; set; } = false;
    public bool LowGraphics { get; set; } = false;
    public System.Collections.ObjectModel.ObservableCollection<FlagEntry> CustomFlags { get; set; } = new();
    public List<string> AppliedFlagKeys { get; set; } = new();

    // Mémoire / CPU
    private int _defRam; public int DefaultRamLimitMb { get => _defRam; set => Set(ref _defRam, value); }  // 0 = illimité
    public RamMode RamMode { get; set; } = RamMode.HardWorkingSet;
    public int RamCheckSeconds { get; set; } = 3;
    public CpuPriority Priority { get; set; } = CpuPriority.Normal;
    public int CoresPerInstance { get; set; } = 0;       // 0 = tous les cœurs

    // Anti-AFK
    public bool AfkEnabled { get; set; } = true;
    public AfkMethod AfkMethod { get; set; } = AfkMethod.Jump;
    public int AfkIntervalSec { get; set; } = 600;
    public int AfkGapSec { get; set; } = 20;          // pause entre deux comptes : un seul compte à la fois
    public int AfkIdleGateSec { get; set; } = 8;      // n'agit que si tu n'as rien touché depuis N secondes

    // Lancement
    public int LaunchDelaySec { get; set; } = 8;
    public bool AutoTile { get; set; } = false;
    public int TileMonitor { get; set; } = 0;
    public int TileColumns { get; set; } = 0;            // 0 = auto
    public int TileDelaySec { get; set; } = 10;
    public int AutoRejoinDelaySec { get; set; } = 15;
    public string RobloxExePath { get; set; } = "";

    // Dernier état de la barre de lancement
    public long LastPlaceId { get; set; }
    public JoinMode LastJoinMode { get; set; } = JoinMode.Place;
    public string LastJoinValue { get; set; } = "";

    // Application
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;     // au démarrage avec Windows : directement dans la barre système
    public bool CloseToTray { get; set; } = true;        // la croix réduit dans la barre système au lieu de quitter
    public int Version { get; set; } = 0;                // migrations de réglages

    // Sécurité
    public bool FirstRunDone { get; set; } = false;      // la question « mot de passe au lancement ? » a déjà été posée
    public int AutoLockMinutes { get; set; } = 10;       // verrouillage après N min sans activité dans l'appli (0 = jamais)
    public bool LockOnTray { get; set; } = true;         // verrouiller quand la fenêtre est masquée dans la barre système
    public bool HideFromCapture { get; set; } = false;   // fenêtre invisible dans les captures / partages d'écran
    public string LastRunVersion { get; set; } = "";     // dernière version lancée : « Nouveautés » ne s'affiche qu'une fois après une mise à jour
    public bool AutoUpdateCheck { get; set; } = true;    // cherche les mises à jour au démarrage puis toutes les 6 h

    // Interface
    public string Accent { get; set; } = "#18B8FF";
    public string SortMode { get; set; } = "Manuel";
    public bool GroupView { get; set; } = true;
    public bool Encrypted { get; set; } = false;         // info : mot de passe maître actif
}

public class LaunchRequest
{
    public JoinMode Mode { get; set; } = JoinMode.Place;
    public long PlaceId { get; set; }
    public string Value { get; set; } = "";
}
