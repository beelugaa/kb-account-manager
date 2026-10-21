using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BBManager.Core;
using BBManager.Models;

namespace BBManager.Services;

/// <summary>Lance les clients Roblox, les suit, applique les limites RAM/CPU, relance et range les fenêtres.</summary>
public class InstanceManager
{
    private readonly AppState _app;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, Process> _procs = new();
    private int _tick;

    public InstanceManager(AppState app)
    {
        _app = app;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private AppSettings S => _app.Settings;

    // =====================================================================
    //  Lancement
    // =====================================================================
    public async Task LaunchAsync(Account acc, LaunchRequest req)
    {
        if (acc.IsBusy) return;
        acc.IsBusy = true;
        try
        {
            var exe = RobloxClient.FindPlayerExe(S) ?? throw new FileNotFoundException(
                "Client Roblox introuvable. Installe Roblox ou indique le chemin dans Paramètres > Lancement.");

            // On ne lance que le vrai client Roblox : signature Authenticode valide, éditeur « Roblox Corporation »
            acc.BusyText = "Vérification du client…";
            string sigDetail = "";
            if (!await Task.Run(() => Authenticode.IsSignedBy(exe, "Roblox Corporation", out sigDetail)))
                throw new InvalidOperationException($"Lancement refusé par sécurité : le client Roblox n'est pas signé par Roblox Corporation ({sigDetail}).");

            if (S.MultiInstance && !MultiInstance.Enable())
                throw new InvalidOperationException(
                    "Un Roblox déjà ouvert bloque le multi-instance. Ferme tous les Roblox (bouton « Fermer tous les Roblox ») puis réessaie.");
            if (!S.MultiInstance && _app.Accounts.Any(a => a != acc && a.IsRunning))
                throw new InvalidOperationException(
                    "Le multi-instance est désactivé et un autre compte est déjà en jeu. Active-le dans Paramètres, ou ferme l'autre instance.");

            if (acc.IsRunning)
            {
                acc.BusyText = "Fermeture de l'instance…";
                KillProcess(acc, userInitiated: false);
                await Task.Delay(1500);
            }

            acc.BusyText = "Préparation du client…";
            RobloxClient.ApplyFlags(S, exe);
            if (S.ForceWindowed) RobloxClient.ForceWindowed();

            acc.BusyText = "Connexion à Roblox…";
            string ticket;
            try { ticket = await RobloxApi.GetAuthTicketAsync(acc.Cookie); }
            catch (UnauthorizedAccessException) { acc.CookieValid = false; throw; }
            acc.CookieValid = true;

            var bt = Random.Shared.NextInt64(100000, 175000) * 1_000_000L + Random.Shared.Next(100000, 999999);
            var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string arg, label;
            if (req.Mode == JoinMode.Home)
            {
                // Ouvre le client sur l'accueil de Roblox, sans rejoindre de jeu.
                label = "accueil de Roblox";
                arg = $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{launchTime}" +
                      $"+browsertrackerid:{bt}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
            }
            else
            {
                acc.BusyText = "Résolution de la cible…";
                var (placeUrl, lbl) = await BuildPlaceLauncherUrlAsync(acc, req);
                label = lbl;
                arg = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{launchTime}" +
                      $"+placelauncherurl:{Uri.EscapeDataString(placeUrl)}+browsertrackerid:{bt}" +
                      "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
            }

            acc.BusyText = "Démarrage de Roblox…";
            var before = RobloxClient.ListPlayers().Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
            var proc = Process.Start(new ProcessStartInfo(exe, arg)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }) ?? throw new InvalidOperationException("Impossible de démarrer RobloxPlayerBeta.exe");

            // Le client peut se relancer lui-même : on suit alors le nouveau processus.
            await Task.Delay(2500);
            if (proc.HasExited)
            {
                Process? found = null;
                for (int i = 0; i < 40 && found == null; i++)
                {
                    foreach (var p in RobloxClient.ListPlayers())
                    {
                        if (!before.Contains(p.Id) && !_procs.Values.Any(t => t.Id == p.Id)) { found = p; break; }
                        p.Dispose();
                    }
                    if (found == null) await Task.Delay(500);
                }
                proc = found ?? throw new InvalidOperationException("Le client Roblox s'est fermé immédiatement (ticket refusé ?).");
            }

            _procs[acc.Id] = proc;
            try { acc.ProcStartUtc = proc.StartTime.ToUniversalTime(); } catch { acc.ProcStartUtc = null; }
            acc.Pid = proc.Id;
            acc.StartedAt = DateTime.Now;
            acc.IsRunning = true;
            acc.UserClosed = false;
            acc.RelaunchAt = null;
            acc.LastUsed = DateTime.Now;
            acc.LastLaunch = req;
            acc.RefreshIdentity();
            acc.NextAfkAt = PickSlot(acc, initial: true);
            ApplyProcessSettings(acc);
            _app.Status($"{acc.Title} lancé · {label}");
            _app.RequestSave();
        }
        catch (Exception ex)
        {
            _app.Status($"Échec du lancement de {acc.Title} : {ex.Message}", error: true);
            throw;
        }
        finally
        {
            acc.IsBusy = false;
            acc.BusyText = "";
            _app.RefreshStats();
        }
    }

    public async Task LaunchManyAsync(IReadOnlyList<Account> accounts, LaunchRequest req, CancellationToken ct = default)
    {
        int ok = 0;
        for (int i = 0; i < accounts.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            try { await LaunchAsync(accounts[i], req); ok++; }
            catch (Exception) { /* statut déjà affiché, on continue avec les suivants */ }
            if (i < accounts.Count - 1 && !ct.IsCancellationRequested)
            {
                for (int s = S.LaunchDelaySec; s > 0 && !ct.IsCancellationRequested; s--)
                {
                    _app.Status($"Compte suivant dans {s}s… ({i + 1}/{accounts.Count})");
                    await Task.Delay(1000);
                }
            }
        }
        _app.Status($"{ok}/{accounts.Count} compte(s) lancé(s).");
        if (S.AutoTile && ok > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, S.TileDelaySec)));
            TileWindows();
        }
    }

    private async Task<(string url, string label)> BuildPlaceLauncherUrlAsync(Account acc, LaunchRequest req)
    {
        long placeId = req.PlaceId != 0 ? req.PlaceId : acc.PlaceId;
        var val = (req.Value ?? "").Trim();
        const string baseUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";
        var bt = Random.Shared.NextInt64(100000, 175000) * 1_000_000L;

        switch (req.Mode)
        {
            case JoinMode.FollowUser:
            {
                long uid;
                if (!long.TryParse(val, out uid))
                {
                    if (!Validate.Username(val)) throw new InvalidOperationException("Pseudo invalide (3 à 20 caractères : lettres, chiffres, _).");
                    uid = await RobloxApi.GetUserIdByNameAsync(val) ?? throw new InvalidOperationException($"Utilisateur « {val} » introuvable.");
                }
                return ($"{baseUrl}?request=RequestFollowUser&browserTrackerId={bt}&userId={uid}&isPlayTogetherGame=false", $"suit l'utilisateur {uid}");
            }
            case JoinMode.JobId:
                if (placeId == 0) throw new InvalidOperationException("Indique un Place ID.");
                if (string.IsNullOrEmpty(val)) throw new InvalidOperationException("Indique un Job ID (ID de serveur).");
                if (!Validate.JobId(val)) throw new InvalidOperationException("Job ID invalide (caractères hexadécimaux et tirets uniquement).");
                return ($"{baseUrl}?request=RequestGameJob&browserTrackerId={bt}&placeId={placeId}&gameId={Uri.EscapeDataString(val)}&isPlayTogetherGame=false", $"serveur {val[..Math.Min(8, val.Length)]}…");
            case JoinMode.PrivateServer:
            {
                if (string.IsNullOrEmpty(val)) throw new InvalidOperationException("Indique le lien ou le code du serveur privé.");
                string code = val;
                var m = System.Text.RegularExpressions.Regex.Match(val, @"privateServerLinkCode=([^&\s]+)");
                if (m.Success) code = m.Groups[1].Value;
                else
                {
                    var sm = System.Text.RegularExpressions.Regex.Match(val, @"[?&]code=([^&\s]+).*type=Server|type=Server.*[?&]code=([^&\s]+)");
                    if (sm.Success)
                    {
                        var shareCode = sm.Groups[1].Success ? sm.Groups[1].Value : sm.Groups[2].Value;
                        if (!Validate.LinkCode(shareCode)) throw new InvalidOperationException("Code de partage invalide.");
                        var r = await RobloxApi.ResolveServerShareLinkAsync(acc.Cookie, shareCode)
                                ?? throw new InvalidOperationException("Lien de partage de serveur privé non résolu.");
                        placeId = r.placeId; code = r.linkCode;
                    }
                    var pm = System.Text.RegularExpressions.Regex.Match(val, @"/games/(\d+)");
                    if (pm.Success && placeId == 0) placeId = long.Parse(pm.Groups[1].Value);
                }
                if (placeId == 0) throw new InvalidOperationException("Indique un Place ID (ou colle le lien complet du serveur privé).");
                if (!Validate.LinkCode(code)) throw new InvalidOperationException("Code de serveur privé invalide.");
                return ($"{baseUrl}?request=RequestPrivateGame&browserTrackerId={bt}&placeId={placeId}&linkCode={Uri.EscapeDataString(code)}&isPlayTogetherGame=false", "serveur privé");
            }
            default:
                if (placeId == 0) throw new InvalidOperationException("Indique un Place ID (jeu à rejoindre).");
                return ($"{baseUrl}?request=RequestGame&browserTrackerId={bt}&placeId={placeId}&isPlayTogetherGame=false", $"place {placeId}");
        }
    }

    // =====================================================================
    //  Fermeture
    // =====================================================================
    public void Close(Account acc) => KillProcess(acc, userInitiated: true);

    public void CloseAll()
    {
        foreach (var a in _app.Accounts.Where(a => a.IsRunning).ToList()) KillProcess(a, true);
    }

    private void KillProcess(Account acc, bool userInitiated)
    {
        if (userInitiated) { acc.UserClosed = true; acc.RelaunchAt = null; }
        if (_procs.TryGetValue(acc.Id, out var p))
        {
            try { if (!p.HasExited) p.Kill(true); }
            catch { /* déjà fermé ou accès refusé : le tick nettoiera */ }
        }
        MarkStopped(acc);
    }

    private void MarkStopped(Account acc)
    {
        if (_procs.Remove(acc.Id, out var p)) p.Dispose();
        if (acc.JobHandle != IntPtr.Zero) { Native.CloseHandle(acc.JobHandle); acc.JobHandle = IntPtr.Zero; }
        acc.HardWsApplied = false;
        acc.NextAfkAt = null; acc.AfkText = ""; acc.ProcStartUtc = null; acc.AfkQueued = false; acc.ProfileRamLimitMb = 0;
        acc.IsRunning = false;
        acc.Pid = 0;
        acc.RamBytes = 0;
        acc.StartedAt = null;
        _app.RefreshStats();
        _app.RequestSave();
    }

    // =====================================================================
    //  Boucle de suivi
    // =====================================================================
    private void OnTick()
    {
        _tick++;
        bool doRam = _tick % Math.Max(1, S.RamCheckSeconds) == 0;

        foreach (var acc in _app.Accounts.ToList())
        {
            if (acc.IsRunning && _procs.TryGetValue(acc.Id, out var p))
            {
                bool exited;
                try { p.Refresh(); exited = p.HasExited; } catch { exited = true; }

                if (exited)
                {
                    bool wasUser = acc.UserClosed;
                    MarkStopped(acc);
                    if (!wasUser && acc.KeepAlive && acc.LastLaunch != null)
                    {
                        acc.RelaunchAt = DateTime.Now.AddSeconds(S.AutoRejoinDelaySec);
                        _app.Status($"{acc.Title} fermé — relance dans {S.AutoRejoinDelaySec}s");
                    }
                    continue;
                }

                acc.TickUptime();
                AfkTick(acc);
                if (doRam)
                {
                    // « Mémoire » du Gestionnaire des tâches = working set privé : c'est cette valeur qu'on affiche et qu'on limite
                    if (!TryMeasure(acc.Pid, out _, out var priv, out var commit))
                    {
                        try { priv = p.WorkingSet64; commit = p.PrivateMemorySize64; } catch { }
                    }
                    acc.RamBytes = priv;
                    acc.CommitBytes = commit;
                    var lim = EffectiveLimitMb(acc);
                    acc.RamRatio = Math.Min(1.0, priv / ((lim > 0 ? lim : 4096) * 1024.0 * 1024.0));
                    EnforceTrim(acc, priv);
                }
            }
            else if (acc.RelaunchAt != null && DateTime.Now >= acc.RelaunchAt && !acc.IsBusy && acc.LastLaunch != null)
            {
                acc.RelaunchAt = null;
                _ = RelaunchAsync(acc);
            }
        }
        if (doRam) _app.RefreshStats();

        if (++_profileTick % 10 == 0 && !_app.ReadOnly) { try { CheckSchedules(); } catch { } }

        // Toutes les 8 s : repérer les Roblox ouverts hors de l'appli, et retenter de prendre le verrou multi-instance
        if (_tick % 8 == 0 && !_app.ReadOnly)
        {
            try { DetectExternal(); } catch { }
            if (S.MultiInstance && !MultiInstance.IsActive && MultiInstance.Enable()) _app.RaiseMultiChanged();
        }
    }

    // =====================================================================
    //  Profils de lancement
    // =====================================================================
    private readonly HashSet<Guid> _profilesRunning = new();

    /// <summary>Lance tous les comptes du profil, un par un, sur le jeu du profil. Les comptes déjà en jeu sont ignorés.</summary>
    public async Task LaunchProfileAsync(LaunchProfile p, CancellationToken ct = default)
    {
        if (!_profilesRunning.Add(p.Id)) { _app.Status($"Le profil « {p.Name} » est déjà en cours de lancement.", true); return; }
        p.IsRunning = true;
        try
        {
            var accounts = _app.Accounts.Where(a => p.AccountIds.Contains(a.Id)).OrderBy(a => a.SortOrder).ToList();
            if (accounts.Count == 0) { _app.Status($"Le profil « {p.Name} » ne contient plus aucun compte.", true); return; }

            foreach (var a in accounts) a.ProfileRamLimitMb = p.RamLimitMb;
            var todo = accounts.Where(a => !a.IsRunning).ToList();
            int skipped = accounts.Count - todo.Count;
            _app.Status($"Profil « {p.Name} » : lancement de {todo.Count} compte(s)" + (skipped > 0 ? $" ({skipped} déjà en jeu)" : "") + "…");

            var req = new LaunchRequest { Mode = p.Mode, PlaceId = p.PlaceId, Value = p.Value };
            bool autoTileBefore = S.AutoTile;
            if (p.TileAfter) S.AutoTile = true;            // la fin de LaunchManyAsync range les fenêtres
            try { await LaunchManyAsync(todo, req, ct); }
            finally { S.AutoTile = autoTileBefore; }

            p.LastRun = DateTime.Now;
            _app.RequestSave();
        }
        finally { p.IsRunning = false; _profilesRunning.Remove(p.Id); }
    }

    private int _profileTick;

    /// <summary>Profils programmés : lance ceux dont l'heure (et le jour) correspondent, une seule fois par jour.</summary>
    private void CheckSchedules()
    {
        var now = DateTime.Now;
        foreach (var p in _app.Profiles.ToList())
        {
            if (!p.ScheduleEnabled || p.IsRunning) continue;
            if (p.ScheduleTime != now.ToString("HH:mm")) continue;
            int bit = ((int)now.DayOfWeek + 6) % 7;                         // lundi = 0
            if (p.ScheduleDays != 0 && (p.ScheduleDays & (1 << bit)) == 0) continue;
            if (p.LastRun?.Date == now.Date && p.LastRun.Value.ToString("HH:mm") == p.ScheduleTime) continue;   // déjà fait cette minute

            _app.Status($"Profil « {p.Name} » : lancement programmé de {p.ScheduleTime}.");
            _ = LaunchProfileAsync(p);
        }
    }

    // =====================================================================
    //  Anti-AFK
    // =====================================================================
    private void AfkTick(Account acc)
    {
        if (!S.AfkEnabled || !acc.AntiAfk)
        {
            acc.AfkText = !S.AfkEnabled ? "Anti-AFK général désactivé" : "Anti-AFK désactivé pour ce compte";
            acc.NextAfkAt = null;
            return;
        }
        var next = acc.NextAfkAt ??= PickSlot(acc, initial: true);
        var left = next - DateTime.Now;
        if (left > TimeSpan.Zero && !acc.AfkQueued) { acc.AfkText = $"Anti-AFK dans {Util.FormatSpan(left)}"; return; }

        // L'heure est venue : on se met dans la file. Un seul compte à la fois, avec une pause entre deux comptes.
        if (!acc.AfkQueued)
        {
            acc.AfkQueued = true;
            _afkQueue.Enqueue(acc);
            StartAfkWorker();
        }
        if (ReferenceEquals(acc, _afkCurrent)) return;          // le texte est géré par le traitement en cours
        int pos = _afkQueue.ToList().IndexOf(acc) + 1;
        acc.AfkText = $"Anti-AFK : en file d'attente (n°{pos})";
    }

    private readonly Queue<Account> _afkQueue = new();
    private Account? _afkCurrent;
    private bool _afkWorker;

    /// <summary>Traite les comptes à tour de rôle : jamais deux fenêtres en même temps, pause entre chaque compte.</summary>
    private async void StartAfkWorker()
    {
        if (_afkWorker) return;
        _afkWorker = true;
        try
        {
            while (_afkQueue.Count > 0)
            {
                var acc = _afkQueue.Dequeue();
                _afkCurrent = acc;
                try { await ProcessAfkAsync(acc); } catch { }
                acc.AfkQueued = false;
                _afkCurrent = null;
                if (_afkQueue.Count > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, S.AfkGapSec)));
            }
        }
        finally { _afkWorker = false; _afkCurrent = null; }
    }

    private async Task ProcessAfkAsync(Account acc)
    {
        // Ne jamais couper l'utilisateur : on attend qu'il n'ait rien touché depuis AfkIdleGateSec secondes.
        // Garde-fou : sans envoi depuis 17 min (Roblox expulse à ~20 min), on passe outre pour ne pas perdre le compte.
        while (acc.IsRunning && S.AfkEnabled && acc.AntiAfk)
        {
            bool force = DateTime.Now - (acc.LastAfkAt ?? acc.StartedAt ?? DateTime.Now) >= TimeSpan.FromMinutes(17);
            if (force || AntiAfk.UserIdleTime() >= TimeSpan.FromSeconds(Math.Max(2, S.AfkIdleGateSec))) break;
            acc.AfkText = "Anti-AFK : en attente que tu sois inactif";
            await Task.Delay(1000);
        }
        if (!acc.IsRunning || !S.AfkEnabled || !acc.AntiAfk) return;

        acc.NextAfkAt = PickSlot(acc, initial: false);
        acc.AfkText = "Anti-AFK : en cours…";
        await SendAfkAsync(acc);                                  // si tu touches à quelque chose : annulé, nouvel essai dans 5 s
    }

    /// <summary>
    /// Prochaine heure d'envoi pour un compte, décalée d'au moins AfkGapSec par rapport aux autres comptes
    /// (intervalle ± 15 % ; au lancement, un point aléatoire dans le premier intervalle).
    /// </summary>
    private DateTime PickSlot(Account me, bool initial)
    {
        int iv = EffectiveAfkInterval(me);
        double sec = initial ? Random.Shared.Next(20, Math.Max(30, iv)) : iv * (0.85 + Random.Shared.NextDouble() * 0.3);
        var gap = Math.Max(5, S.AfkGapSec);
        var t = DateTime.Now.AddSeconds(sec);
        for (int i = 0; i < 40; i++)
        {
            bool clash = _app.Accounts.Any(a => a != me && a.IsRunning && a.NextAfkAt != null && Math.Abs((a.NextAfkAt.Value - t).TotalSeconds) < gap);
            if (!clash) break;
            t = t.AddSeconds(gap);
        }
        return t;
    }

    private async Task<bool> SendAfkAsync(Account acc)
    {
        var hwnd = FindMainWindow(acc.Pid);
        AntiAfk.AfkResult res = AntiAfk.AfkResult.NoWindow;
        try { res = await AntiAfk.SendAsync(hwnd, EffectiveAfkMethod(acc)); } catch { }
        switch (res)
        {
            case AntiAfk.AfkResult.Sent:
                acc.LastAfkAt = DateTime.Now;
                acc.AfkText = $"Anti-AFK envoyé à {DateTime.Now:HH:mm:ss}";
                break;
            case AntiAfk.AfkResult.Aborted:
                acc.NextAfkAt = DateTime.Now.AddSeconds(5);   // tu as touché à quelque chose : on réessaie très vite
                acc.AfkText = "Anti-AFK annulé (tu étais actif), nouvel essai…";
                break;
            default:
                acc.AfkText = "Anti-AFK : fenêtre introuvable";
                break;
        }
        return res == AntiAfk.AfkResult.Sent;
    }

    public AfkMethod EffectiveAfkMethod(Account acc) => acc.AfkMethodOverride ?? S.AfkMethod;

    public int EffectiveAfkInterval(Account acc) => Math.Max(15, acc.AfkIntervalSec > 0 ? acc.AfkIntervalSec : S.AfkIntervalSec);

    /// <summary>Envoie tout de suite l'anti-AFK aux comptes donnés (un par un : chacun prend brièvement le focus).</summary>
    public async Task<(int ok, int total)> SendAfkNowAsync(IEnumerable<Account> accounts)
    {
        var running = accounts.Where(a => a.IsRunning).ToList();
        int ok = 0;
        for (int i = 0; i < running.Count; i++)
        {
            var a = running[i];
            if (await SendAfkAsync(a)) ok++;
            a.NextAfkAt = PickSlot(a, initial: false);
            if (i < running.Count - 1) await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(2, S.AfkGapSec))));   // petite pause entre deux fenêtres
        }
        return (ok, running.Count);
    }

    private async Task RelaunchAsync(Account acc)
    {
        try { await LaunchAsync(acc, acc.LastLaunch!); }
        catch { acc.RelaunchAt = DateTime.Now.AddSeconds(Math.Max(30, S.AutoRejoinDelaySec * 2)); }
    }

    // =====================================================================
    //  Limites RAM / CPU
    // =====================================================================
    public int EffectiveLimitMb(Account acc) => acc.ProfileRamLimitMb > 0 ? acc.ProfileRamLimitMb : acc.RamLimitMb > 0 ? acc.RamLimitMb : S.DefaultRamLimitMb;

    private void EnforceTrim(Account acc, long workingSet)
    {
        if (S.RamMode != RamMode.Trim) return;
        var limit = EffectiveLimitMb(acc);
        if (limit <= 0 || workingSet <= limit * 1024L * 1024L) return;

        var h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, acc.Pid);
        if (h == IntPtr.Zero) return;
        try
        {
            if (!Native.SetProcessWorkingSetSizeEx(h, UIntPtr.MaxValue, UIntPtr.MaxValue, 0) && !acc.LimitWarned)
            {
                acc.LimitWarned = true;
                _app.Status($"Impossible de réduire la RAM de {acc.Title} (accès refusé par Windows/Roblox).", true);
            }
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Mesure mémoire : working set total, working set privé (= colonne « Mémoire » du Gestionnaire) et mémoire engagée.</summary>
    private static bool TryMeasure(int pid, out long totalWs, out long privateWs, out long commit)
    {
        totalWs = privateWs = commit = 0;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var c = new Native.PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>() };
            if (!Native.GetProcessMemoryInfo(h, out c, c.cb)) return false;
            totalWs = (long)(ulong)c.WorkingSetSize;
            privateWs = (long)c.PrivateWorkingSetSize;
            commit = (long)(ulong)c.PrivateUsage;
            return privateWs > 0;
        }
        finally { Native.CloseHandle(h); }
    }

    // =====================================================================
    //  Reprise des instances après un redémarrage de l'appli
    // =====================================================================
    public List<SessionRecord> SnapshotSessions() => _app.Accounts
        .Where(a => a.IsRunning && a.ProcStartUtc != null)
        .Select(a => new SessionRecord
        {
            AccountId = a.Id, Pid = a.Pid, ProcessStartUtcTicks = a.ProcStartUtc!.Value.Ticks, StartedAt = a.StartedAt ?? DateTime.Now,
            Mode = a.LastLaunch?.Mode ?? JoinMode.Place, PlaceId = a.LastLaunch?.PlaceId ?? 0, Value = a.LastLaunch?.Value ?? "",
        }).ToList();

    /// <summary>Rattache aux comptes les Roblox encore ouverts lors de la dernière session (même PID et même heure de démarrage).</summary>
    public int Reattach(IEnumerable<SessionRecord> sessions)
    {
        int n = 0;
        foreach (var s in sessions)
        {
            var acc = _app.Accounts.FirstOrDefault(a => a.Id == s.AccountId);
            if (acc == null || acc.IsRunning) continue;

            Process p;
            try { p = Process.GetProcessById(s.Pid); } catch { continue; }
            try
            {
                var started = p.StartTime.ToUniversalTime();
                bool same = p.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(started.Ticks - s.ProcessStartUtcTicks) < TimeSpan.FromSeconds(2).Ticks;
                if (!same) { p.Dispose(); continue; }

                Attach(acc, p, started, s.StartedAt, new LaunchRequest { Mode = s.Mode, PlaceId = s.PlaceId, Value = s.Value });
                n++;
            }
            catch { p.Dispose(); }
        }
        _app.RefreshStats();
        return n;
    }

    private void Attach(Account acc, Process p, DateTime startUtc, DateTime startedAt, LaunchRequest? req)
    {
        _procs[acc.Id] = p;
        acc.ProcStartUtc = startUtc;
        acc.Pid = p.Id;
        acc.StartedAt = startedAt;
        acc.IsRunning = true;
        acc.UserClosed = false;
        acc.LastLaunch = req;
        acc.NextAfkAt = PickSlot(acc, initial: true);
        ApplyProcessSettings(acc);
    }

    // =====================================================================
    //  Détection des Roblox déjà ouverts (lancés hors de l'appli ou par une ancienne version)
    // =====================================================================
    private readonly Dictionary<int, int> _detectTries = new();

    /// <summary>
    /// Pour chaque client Roblox non suivi, lit son journal pour retrouver le compte connecté
    /// et le rattache au compte correspondant de l'appli. Retourne le nombre de comptes détectés.
    /// </summary>
    public int DetectExternal()
    {
        var managed = _procs.Values.Select(p => { try { return p.Id; } catch { return -1; } }).ToHashSet();
        var found = new List<(Process p, DateTime start, FileInfo? log)>();

        foreach (var p in RobloxClient.ListPlayers())
        {
            if (managed.Contains(p.Id)) { p.Dispose(); continue; }
            _detectTries.TryGetValue(p.Id, out var tries);
            if (tries >= 90) { p.Dispose(); continue; }          // on arrête d'essayer après ~15 min
            _detectTries[p.Id] = tries + 1;
            try { var st = p.StartTime.ToUniversalTime(); found.Add((p, st, RobloxLogs.FindLog(st))); }
            catch { p.Dispose(); }
        }

        // Deux clients démarrés à quelques secondes d'écart pourraient pointer vers le même journal : on ne devine pas
        var ambiguous = found.Where(f => f.log != null).GroupBy(f => f.log!.FullName).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(x => x.p.Id)).ToHashSet();

        int n = 0;
        foreach (var (p, start, log) in found)
        {
            try
            {
                if (log == null || ambiguous.Contains(p.Id)) { p.Dispose(); continue; }
                var ident = RobloxLogs.ParseIdentity(log);
                var acc = ident == null ? null : _app.Accounts.FirstOrDefault(a => a.UserId == ident.UserId && !a.IsRunning && !a.IsBusy);
                if (acc == null) { p.Dispose(); continue; }

                Attach(acc, p, start, start.ToLocalTime(),
                    ident!.PlaceId != 0 ? new LaunchRequest { Mode = JoinMode.Place, PlaceId = ident.PlaceId } : null);
                _app.Status($"{acc.Title} détecté en jeu (PID {p.Id}) : reconnu automatiquement.");
                n++;
            }
            catch { p.Dispose(); }
        }
        if (n > 0) { _app.RefreshStats(); _app.RequestSave(); }
        return n;
    }

    /// <summary>Nombre de clients Roblox ouverts que l'appli ne gère pas (lancés hors de l'appli).</summary>
    public int CountUnmanaged()
    {
        var managed = _procs.Values.Select(p => { try { return p.Id; } catch { return -1; } }).ToHashSet();
        int n = 0;
        foreach (var p in RobloxClient.ListPlayers()) { if (!managed.Contains(p.Id)) n++; p.Dispose(); }
        return n;
    }

    /// <summary>Applique priorité, cœurs et limite RAM (modes strict / working set fixe) à une instance.</summary>
    public void ApplyProcessSettings(Account acc)
    {
        if (!acc.IsRunning || acc.Pid == 0) return;

        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_QUOTA |
                                   Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, acc.Pid);
        if (h == IntPtr.Zero)
        {
            _app.Status($"Accès refusé au processus de {acc.Title} (limites non appliquées).", error: true);
            return;
        }
        try
        {
            // Priorité
            uint prio = S.Priority switch
            {
                CpuPriority.Idle => Native.IDLE_PRIORITY_CLASS,
                CpuPriority.BelowNormal => Native.BELOW_NORMAL_PRIORITY_CLASS,
                CpuPriority.AboveNormal => Native.ABOVE_NORMAL_PRIORITY_CLASS,
                _ => Native.NORMAL_PRIORITY_CLASS,
            };
            Native.SetPriorityClass(h, prio);

            // Cœurs dédiés
            int cpu = Environment.ProcessorCount;
            if (S.CoresPerInstance > 0 && S.CoresPerInstance < cpu)
            {
                int slot = _app.Accounts.Where(a => a.IsRunning && a.StartedAt != null && a.StartedAt < acc.StartedAt).Count();
                ulong mask = 0;
                for (int i = 0; i < S.CoresPerInstance; i++) mask |= 1UL << ((slot * S.CoresPerInstance + i) % cpu);
                Native.SetProcessAffinityMask(h, (UIntPtr)mask);
            }
            else
            {
                ulong all = cpu >= 64 ? ulong.MaxValue : (1UL << cpu) - 1;
                Native.SetProcessAffinityMask(h, (UIntPtr)all);
            }

            // RAM
            var limitMb = EffectiveLimitMb(acc);
            var bytes = (UIntPtr)((ulong)Math.Max(0, limitMb) * 1024UL * 1024UL);

            if (S.RamMode == RamMode.HardWorkingSet && limitMb > 0)
            {
                if (Native.SetProcessWorkingSetSizeEx(h, (UIntPtr)(1024 * 1024), bytes,
                        Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE))
                    acc.HardWsApplied = true;
                else
                    _app.Status($"Limite RAM refusée pour {acc.Title} (erreur Windows {Marshal.GetLastWin32Error()}).", true);
            }
            else if (acc.HardWsApplied)
            {
                Native.SetProcessWorkingSetSizeEx(h, UIntPtr.MaxValue, UIntPtr.MaxValue,
                    Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE);
                acc.HardWsApplied = false;
            }

            ApplyJobLimit(acc, h, S.RamMode == RamMode.JobObject ? limitMb : 0);
        }
        finally { Native.CloseHandle(h); }
    }

    private void ApplyJobLimit(Account acc, IntPtr hProcess, int limitMb)
    {
        if (limitMb <= 0 && acc.JobHandle == IntPtr.Zero) return;

        if (acc.JobHandle == IntPtr.Zero)
        {
            acc.JobHandle = Native.CreateJobObject(IntPtr.Zero, null);
            if (acc.JobHandle == IntPtr.Zero) return;
            if (!Native.AssignProcessToJobObject(acc.JobHandle, hProcess))
            {
                Native.CloseHandle(acc.JobHandle); acc.JobHandle = IntPtr.Zero;
                _app.Status($"Impossible d'assigner {acc.Title} à un Job Object.", error: true);
                return;
            }
        }

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        if (limitMb > 0)
        {
            info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = (UIntPtr)((ulong)limitMb * 1024UL * 1024UL);
        }
        int size = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            Native.SetInformationJobObject(acc.JobHandle, Native.JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Vide immédiatement le working set de toutes les instances. Retourne le nombre d'instances traitées.</summary>
    public int TrimAllNow()
    {
        int n = 0;
        foreach (var acc in _app.Accounts.Where(a => a.IsRunning))
        {
            var h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, acc.Pid);
            if (h == IntPtr.Zero) continue;
            try { if (Native.SetProcessWorkingSetSizeEx(h, UIntPtr.MaxValue, UIntPtr.MaxValue, 0)) n++; }
            finally { Native.CloseHandle(h); }
        }
        return n;
    }

    public void ApplyAll()
    {
        foreach (var a in _app.Accounts.Where(a => a.IsRunning)) ApplyProcessSettings(a);
    }

    // =====================================================================
    //  Fenêtres
    // =====================================================================
    public static IntPtr FindMainWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var wp);
            if (wp == (uint)pid && Native.IsWindowVisible(h) && Native.GetWindow(h, Native.GW_OWNER) == IntPtr.Zero
                && Native.GetWindowTextLength(h) > 0)
            {
                found = h; return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void Focus(Account acc)
    {
        if (!acc.IsRunning) return;
        var h = FindMainWindow(acc.Pid);
        if (h == IntPtr.Zero) return;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        Native.SetForegroundWindow(h);
    }

    public static List<Native.RECT> GetMonitors()
    {
        var list = new List<Native.RECT>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT __, IntPtr ___) =>
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(hMon, ref mi)) list.Add(mi.rcWork);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Range toutes les fenêtres Roblox gérées en grille sur l'écran choisi.</summary>
    public void TileWindows()
    {
        var running = _app.Accounts.Where(a => a.IsRunning).OrderBy(a => a.StartedAt).ToList();
        if (running.Count == 0) { _app.Status("Aucune instance à ranger."); return; }

        var mons = GetMonitors();
        if (mons.Count == 0) return;
        var area = mons[Math.Clamp(S.TileMonitor, 0, mons.Count - 1)];
        int w = area.Right - area.Left, h = area.Bottom - area.Top;

        int n = running.Count;
        int cols = S.TileColumns > 0 ? Math.Min(S.TileColumns, n) : (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        int cw = w / cols, ch = h / rows;

        int moved = 0;
        for (int i = 0; i < n; i++)
        {
            var hwnd = FindMainWindow(running[i].Pid);
            if (hwnd == IntPtr.Zero) continue;
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            int x = area.Left + (i % cols) * cw, y = area.Top + (i / cols) * ch;
            if (Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, cw, ch, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW)) moved++;
        }
        _app.Status($"{moved}/{n} fenêtre(s) rangée(s) en grille {cols}×{rows}.");
    }
}
