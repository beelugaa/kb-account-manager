using System.Collections.ObjectModel;
using System.Windows.Threading;
using BBManager.Core;
using BBManager.Models;

namespace BBManager.Services;

/// <summary>État global de l'application : comptes, groupes, jeux, réglages et instances.</summary>
public class AppState : ObservableObject
{
    public static AppState Current { get; private set; } = null!;

    public ObservableCollection<Account> Accounts { get; }
    public ObservableCollection<AccountGroup> Groups { get; }
    public ObservableCollection<SavedPlace> Places { get; }
    public ObservableCollection<LaunchProfile> Profiles { get; }
    public AppSettings Settings { get; }
    public InstanceManager Instances { get; }
    public UpdateService Updates { get; } = new();

    private readonly DispatcherTimer _saveTimer;

    /// <summary>Levée quand les groupes ou leur contenu changent (reconstruit les pastilles de filtre).</summary>
    public event Action? GroupsChanged;

    public AppState(DataFile data, AppSettings settings)
    {
        Current = this;
        Settings = settings;
        Accounts = new(data.Accounts.OrderBy(a => a.SortOrder));
        Groups = new(data.Groups.OrderBy(g => g.SortOrder));
        Places = new(data.Places);
        Profiles = new(data.Profiles);
        RebuildGroupInfo();
        RefreshProfiles();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        Instances = new InstanceManager(this);
    }

    /// <summary>Le verrou multi-instance a changé d'état : rafraîchit la carte en bas à gauche.</summary>
    public void RaiseMultiChanged() => Raise("MultiState");

    // ---- Statut / stats -----------------------------------------------------
    private string _status = "Prêt.";
    private bool _statusError;
    private string _stats = "";

    public string StatusMessage { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusError; private set => Set(ref _statusError, value); }
    public string StatsText { get => _stats; private set => Set(ref _stats, value); }
    public int RunningCount => Accounts.Count(a => a.IsRunning);

    public void Status(string msg, bool error = false)
    {
        StatusMessage = msg;
        StatusIsError = error;
    }

    public void RefreshStats()
    {
        var running = Accounts.Where(a => a.IsRunning).ToList();
        long ram = running.Sum(a => a.RamBytes);
        StatsText = running.Count == 0
            ? $"{Accounts.Count} compte(s)"
            : $"{Accounts.Count} compte(s) · {running.Count} en jeu · {Util.FormatBytes(ram)} de RAM";
        Raise(nameof(RunningCount));
    }

    // ---- Sauvegarde ---------------------------------------------------------
    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Mode démo (--demo) : données factices, rien n'est écrit sur le disque.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Après une réinitialisation : plus aucune écriture (sinon les données effacées seraient recréées en quittant).</summary>
    public static bool SaveDisabled { get; set; }

    public void SaveNow()
    {
        if (ReadOnly || SaveDisabled) return;
        try
        {
            for (int i = 0; i < Groups.Count; i++) Groups[i].SortOrder = i;
            DataStore.Save(new DataFile
            {
                Accounts = Accounts.ToList(), Groups = Groups.ToList(), Places = Places.ToList(), Profiles = Profiles.ToList(),
                Sessions = Instances?.SnapshotSessions() ?? new(),
            });
            DataStore.SaveSettings(Settings);
        }
        catch (Exception ex) { Status("Erreur de sauvegarde : " + ex.Message, true); }
    }

    // ---- Groupes ------------------------------------------------------------
    public void RebuildGroupInfo()
    {
        var map = Groups.Select((g, i) => (g, i)).ToDictionary(x => x.g.Id);
        foreach (var a in Accounts)
        {
            if (a.GroupId != null && map.TryGetValue(a.GroupId.Value, out var x))
            { a.GroupName = x.g.Name; a.GroupColor = x.g.Color; a.GroupSort = x.i; }
            else
            { a.GroupName = "Sans groupe"; a.GroupColor = "#6B7080"; a.GroupSort = int.MaxValue; }
        }
        foreach (var g in Groups) g.Count = Accounts.Count(a => a.GroupId == g.Id);
        RefreshStats();
        GroupsChanged?.Invoke();
    }

    public AccountGroup AddGroup(string name, string color)
    {
        var g = new AccountGroup { Name = name, Color = color, SortOrder = Groups.Count };
        Groups.Add(g);
        RebuildGroupInfo(); RequestSave();
        return g;
    }

    public void DeleteGroup(AccountGroup g)
    {
        foreach (var a in Accounts.Where(a => a.GroupId == g.Id)) a.GroupId = null;
        Groups.Remove(g);
        RebuildGroupInfo(); RequestSave();
    }

    public void MoveGroup(AccountGroup g, int delta)
    {
        int i = Groups.IndexOf(g), j = i + delta;
        if (i < 0 || j < 0 || j >= Groups.Count) return;
        Groups.Move(i, j);
        RebuildGroupInfo(); RequestSave();
    }

    public void SetGroup(IEnumerable<Account> accounts, Guid? groupId)
    {
        foreach (var a in accounts) a.GroupId = groupId;
        RebuildGroupInfo(); RequestSave();
    }

    // ---- Comptes ------------------------------------------------------------
    public async Task<(Account account, bool updated)> AddAccountAsync(string rawCookie, Guid? groupId)
    {
        var cookie = RobloxApi.NormalizeCookie(rawCookie);
        if (cookie.Length < 100 || !cookie.Contains("_|WARNING"))
            throw new InvalidOperationException("Ce n'est pas un cookie .ROBLOSECURITY valide (il doit commencer par _|WARNING:-DO-NOT-SHARE-THIS…).");

        var user = await RobloxApi.GetAuthenticatedUserAsync(cookie)
                   ?? throw new InvalidOperationException("Cookie refusé par Roblox (expiré ou invalide).");

        var existing = Accounts.FirstOrDefault(a => a.UserId == user.Id);
        bool updated = existing != null;
        var acc = existing ?? new Account { SortOrder = (Accounts.Count == 0 ? 0 : Accounts.Max(a => a.SortOrder) + 1) };
        acc.Cookie = cookie;
        acc.UserId = user.Id;
        acc.Username = user.Name;
        acc.DisplayName = user.DisplayName;
        acc.CookieValid = true;
        if (!updated)
        {
            acc.GroupId = groupId;
            Accounts.Add(acc);
        }
        acc.AvatarUrl = await RobloxApi.GetAvatarUrlAsync(user.Id);
        acc.Robux = await RobloxApi.GetRobuxAsync(cookie);
        acc.RefreshIdentity();
        RebuildGroupInfo(); RequestSave();
        return (acc, updated);
    }

    public async Task RefreshAccountAsync(Account acc)
    {
        if (acc.IsBusy) return;
        acc.IsBusy = true; acc.BusyText = "Vérification…";
        try
        {
            var user = await RobloxApi.GetAuthenticatedUserAsync(acc.Cookie);
            if (user == null) { acc.CookieValid = false; return; }
            acc.CookieValid = true;
            acc.Username = user.Name; acc.DisplayName = user.DisplayName;
            acc.Robux = await RobloxApi.GetRobuxAsync(acc.Cookie);
            var av = await RobloxApi.GetAvatarUrlAsync(user.Id);
            if (!string.IsNullOrEmpty(av)) acc.AvatarUrl = av;
            acc.RefreshIdentity();
        }
        catch (Exception ex) { Status($"Vérification de {acc.Title} impossible : {ex.Message}", true); }
        finally { acc.IsBusy = false; acc.BusyText = ""; RequestSave(); }
    }

    public async Task RefreshAllAsync()
    {
        Status("Vérification de tous les comptes…");
        var list = Accounts.ToList();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(list.Select(async a =>
        {
            await gate.WaitAsync();
            try { await RefreshAccountAsync(a); } finally { gate.Release(); }
        }));
        int bad = list.Count(a => !a.CookieValid);
        Status(bad == 0 ? $"{list.Count} compte(s) vérifié(s), tout est OK." : $"{bad} compte(s) avec un cookie invalide.", bad > 0);
    }

    public void DeleteAccounts(IEnumerable<Account> accounts)
    {
        foreach (var a in accounts.ToList())
        {
            if (a.IsRunning) Instances.Close(a);
            Accounts.Remove(a);
        }
        foreach (var pr in Profiles) pr.AccountIds.RemoveAll(id => Accounts.All(a => a.Id != id));
        RefreshProfiles();
        RebuildGroupInfo(); RequestSave();
    }

    /// <summary>Place <paramref name="dragged"/> juste avant <paramref name="target"/> dans l'ordre manuel.</summary>
    public void ReorderBefore(Account dragged, Account target)
    {
        if (dragged == target) return;
        var ordered = Accounts.OrderBy(a => a.SortOrder).ToList();
        ordered.Remove(dragged);
        ordered.Insert(ordered.IndexOf(target), dragged);
        for (int i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;
        dragged.GroupId = target.GroupId;
        RebuildGroupInfo(); RequestSave();
    }

    // ---- Profils ------------------------------------------------------------
    private static readonly string[] DayNames = { "lun", "mar", "mer", "jeu", "ven", "sam", "dim" };

    /// <summary>Recalcule les textes affichés de chaque profil (comptes, jeu, programmation).</summary>
    public void RefreshProfiles()
    {
        foreach (var p in Profiles)
        {
            var names = Accounts.Where(a => p.AccountIds.Contains(a.Id)).OrderBy(a => a.SortOrder).Select(a => a.Title).ToList();
            int missing = p.AccountIds.Count - names.Count;
            p.AccountsText = names.Count == 0 ? "Aucun compte" :
                $"{names.Count} compte(s) : " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" +{names.Count - 4}" : "") +
                (missing > 0 ? $"  ({missing} supprimé(s))" : "");

            p.GameText = p.Mode switch
            {
                JoinMode.Home => "Accueil de Roblox",
                JoinMode.FollowUser => $"Rejoint {p.Value}",
                JoinMode.PrivateServer => "Serveur privé",
                JoinMode.JobId => "Serveur précis",
                _ => Places.FirstOrDefault(x => x.PlaceId == p.PlaceId) is { } pl ? pl.Label : (p.PlaceId == 0 ? "Jeu de chaque compte" : $"Place {p.PlaceId}"),
            };

            if (!p.ScheduleEnabled) p.ScheduleText = "Pas de programmation";
            else
            {
                var days = p.ScheduleDays == 0 ? "tous les jours"
                    : string.Join(", ", Enumerable.Range(0, 7).Where(i => (p.ScheduleDays & (1 << i)) != 0).Select(i => DayNames[i]));
                p.ScheduleText = $"Programmé à {p.ScheduleTime} · {days}";
            }
        }
    }

    public void DeleteProfile(LaunchProfile p) { Profiles.Remove(p); RequestSave(); }

    // ---- Jeux enregistrés ---------------------------------------------------
    public async Task<SavedPlace> AddPlaceAsync(long placeId, string? name = null)
    {
        var existing = Places.FirstOrDefault(p => p.PlaceId == placeId);
        if (existing != null) return existing;
        var p = new SavedPlace { PlaceId = placeId, Name = name ?? "" };
        Places.Add(p);
        RequestSave();
        await FillPlaceAsync(p);
        return p;
    }

    /// <summary>Complète le nom et l'icône d'un jeu enregistré (ne remplace pas un nom que tu as saisi toi-même).</summary>
    private async Task FillPlaceAsync(SavedPlace p)
    {
        var info = await RobloxApi.GetGameInfoAsync(p.PlaceId);
        if (info == null) return;
        if (string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(info.Name)) p.Name = info.Name;
        if (info.IconUrl.Length > 0) p.IconUrl = info.IconUrl;
        RequestSave();
    }

    /// <summary>Redemande le nom et l'icône à Roblox (le nom est remplacé, contrairement à l'ajout).</summary>
    public async Task RefreshPlaceAsync(SavedPlace p)
    {
        var info = await RobloxApi.GetGameInfoAsync(p.PlaceId);
        if (info == null) { Status("Impossible de joindre Roblox pour ce jeu.", true); return; }
        if (!string.IsNullOrWhiteSpace(info.Name)) p.Name = info.Name;
        if (info.IconUrl.Length > 0) p.IconUrl = info.IconUrl;
        RequestSave();
    }

    /// <summary>Au démarrage : récupère le nom et l'image des jeux enregistrés qui n'en ont pas encore (jeux ajoutés avant cette fonction).</summary>
    public async Task RefreshPlacesAsync(bool all = false)
    {
        foreach (var p in Places.Where(x => all || !x.HasIcon || string.IsNullOrWhiteSpace(x.Name)).ToList())
        {
            await FillPlaceAsync(p);
            await Task.Delay(250);
        }
    }
}
