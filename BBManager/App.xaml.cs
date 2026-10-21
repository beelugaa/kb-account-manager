using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BBManager.Core;
using BBManager.Models;
using BBManager.Services;
using BBManager.Views;

namespace BBManager;

public partial class App : Application
{
    private Mutex? _single;

    public static bool Exiting { get; private set; }
    public static TrayService? Tray { get; private set; }

    private static bool _restart;

    /// <summary>Après « Réinitialiser l'appli » : on ne réécrit rien sur le disque, puis on relance une instance toute neuve.</summary>
    public static void ResetAndRestart()
    {
        DataStore.ResetAll();
        AppState.SaveDisabled = true;
        _restart = true;
        Exiting = true;
        Current.Shutdown();
    }

    /// <summary>Quitte vraiment l'appli (la croix, elle, réduit dans la barre système). Les Roblox restent ouverts.</summary>
    public static void Quit()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProcessHardening.Apply();                                // DLL, images distantes, accès mémoire depuis les autres programmes

        bool isDemo = e.Args.Contains("--demo");
        var suffix = isDemo ? "Demo" : Environment.GetEnvironmentVariable("KBM_DATA") is { Length: > 0 } ? "Custom" : "Main";   // données alternatives = instance indépendante
        _single = new Mutex(true, "BBManager.SingleInstance." + suffix, out var first);
        if (!first)
        {
            // Déjà ouvert (peut-être dans la barre système) : on le réveille au lieu d'afficher une erreur
            try { EventWaitHandle.OpenExisting("KBManager.Show." + suffix).Set(); } catch { }
            Shutdown();
            return;
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "KBManager.Show." + suffix);
        new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                Dispatcher.BeginInvoke(() => Tray?.ShowWindow());
            }
        }) { IsBackground = true }.Start();

        SessionEnding += (_, _) => Exiting = true;

        DispatcherUnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Util.AppDir, "crash.log"), $"[{DateTime.Now:u}] {ex.Exception}{Environment.NewLine}{Environment.NewLine}"); } catch { }
            MessageBox.Show(ex.Exception.ToString(), "Erreur inattendue", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // Barre de titre sombre pour toutes les fenêtres
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w)
            {
                int on = 1;
                Native.DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, sizeof(int));
            }
        }));

        bool demo = isDemo;
        var settings = demo ? new AppSettings() : DataStore.LoadSettings();

        // Déverrouillage / chargement des données
        DataFile data = null!;
        while (!demo)
        {
            try
            {
                if (DataStore.IsPasswordProtected())
                {
                    DataFile? loaded = null;
                    var dlg = new PasswordDialog(PasswordDialog.PasswordMode.Unlock, pwd =>
                    {
                        try { loaded = DataStore.Load(pwd); return true; }
                        catch (CryptographicException) { return false; }          // mauvais mot de passe ou fichier modifié
                    });
                    bool? res = dlg.ShowDialog();
                    if (dlg.ResetRequested) { DataStore.ResetAll(); settings = new AppSettings(); continue; }   // « J'ai oublié mon mot de passe » → réinitialiser
                    if (res != true || loaded == null) { Shutdown(); return; }
                    data = loaded;
                }
                else data = DataStore.Load(null);
                break;
            }
            catch (Exception ex) when (ex is CryptographicException or System.IO.InvalidDataException or System.Text.Json.JsonException)
            {
                // Fichier illisible (autre session Windows, corrompu ou modifié) : on propose de repartir de zéro
                var why = ex is CryptographicException ? "Elles ont été créées avec une autre session Windows." : "Le fichier est corrompu ou a été modifié.";
                if (MessageDialog.Show(null, "Données illisibles",
                        $"Impossible de lire les comptes enregistrés. {why}\nTu peux réinitialiser l'appli : tout ce qui est enregistré est supprimé.",
                        "Réinitialiser l'appli", cancel: true, danger: true))
                { DataStore.ResetAll(); settings = new AppSettings(); continue; }
                Shutdown(); return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossible de lire les données :\n" + ex.Message, "KB Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(); return;
            }
        }

        if (settings.Version < 2)
        {
            // v2 : la limite de RAM « souple » se faisait re-gonfler par Roblox en quelques secondes ; plafond permanent par défaut
            settings.RamMode = RamMode.HardWorkingSet;
            settings.Version = 2;
        }
        ApplyAccent(settings.Accent);

        // Tout premier lancement : proposer un mot de passe au démarrage de l'appli
        bool firstRunChanged = false;
        if (!demo && !settings.FirstRunDone)
        {
            if (!DataStore.HasPassword)
            {
                var chosen = FirstRunDialog.Ask();
                if (chosen != null) DataStore.SetPassword(chosen);
            }
            settings.FirstRunDone = true;
            firstRunChanged = true;
        }

        if (demo) data = DemoData();
        settings.Encrypted = DataStore.HasPassword;
        var app = new AppState(data, settings) { ReadOnly = demo };
        if (firstRunChanged) app.SaveNow();                     // écrit tout de suite les données sous la nouvelle protection
        if (demo) SeedDemoRuntime(app);

        _ = app.RefreshPlacesAsync();                          // noms et images des jeux favoris qui en manquent (en arrière-plan)

        if (settings.MultiInstance) MultiInstance.Enable();   // sinon la carte en bas à gauche explique pourquoi, et réessaie toute seule

        // Reprise des instances encore ouvertes depuis la dernière session
        if (!demo)
        {
            int resumed = app.Instances.Reattach(data.Sessions);
            int detected = app.Instances.DetectExternal();
            if (resumed + detected > 0) app.Status($"{resumed + detected} instance(s) en jeu reconnue(s) automatiquement.");
            settings.StartWithWindows = BBManager.Services.Startup.IsEnabled();
            BBManager.Services.Startup.RefreshPath();
        }

        var win = new MainWindow(app);
        var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page="))?[7..];
        if (pageArg == "profiles") win.NavProfiles.IsChecked = true;
        else if (pageArg == "instances") win.NavInstances.IsChecked = true;
        else if (pageArg == "afk") win.NavAfk.IsChecked = true;
        else if (pageArg == "settings") win.NavSettings.IsChecked = true;
        MainWindow = win;

        if (!demo)
        {
            Tray = new TrayService(app, win);
            app.Updates.UpdateFound += info => Tray?.NotifyUpdate(info);
            app.Updates.StartAutoCheck(() => app.Settings.AutoUpdateCheck);
        }
        bool startHidden = !demo && e.Args.Contains("--tray") && settings.StartMinimized;
        if (!startHidden) win.Show();

        // « Nouveautés » : une seule fois après une mise à jour (pas à la toute première installation)
        if (!demo && !startHidden && settings.LastRunVersion != UpdateService.CurrentText)
        {
            bool existingUser = settings.LastRunVersion.Length > 0 || !firstRunChanged;   // première installation : FirstRunDone vient d'être posé
            settings.LastRunVersion = UpdateService.CurrentText;
            app.SaveNow();
            if (existingUser) new WhatsNewDialog { Owner = win }.ShowDialog();
        }

        // Aide au développement : --demo --open=add|edit|group|msg|password ouvre une boîte de dialogue non modale.
        var openArg = e.Args.FirstOrDefault(a => a.StartsWith("--open="))?[7..];
        if (demo && openArg != null)
        {
            Window? w = openArg switch
            {
                "add" => new AddAccountsDialog(app, null),
                "edit" => new EditAccountDialog(app, app.Accounts[0]),
                "group" => new GroupDialog(),
                "whatsnew" => new WhatsNewDialog(),
                "password" => new PasswordDialog(PasswordDialog.PasswordMode.Set),
                _ => null,
            };
            if (w != null) { w.Owner = win; w.Show(); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppState.Current?.SaveNow(); } catch { }
        try { SecureClipboard.ClearIfOurs(); } catch { }
        try { Tray?.Dispose(); } catch { }
        if (_restart)
        {
            try { _single?.ReleaseMutex(); _single?.Dispose(); } catch { }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }); } catch { }
        }
        // Le mutex multi-instance est libéré à la fermeture ; les clients déjà lancés continuent de tourner.
        base.OnExit(e);
    }

    private static DataFile DemoData()
    {
        var main = new AccountGroup { Name = "Principaux", Color = "#7C5CFF", SortOrder = 0 };
        var alts = new AccountGroup { Name = "Alts farm", Color = "#3DDC97", SortOrder = 1 };
        var data = new DataFile { Groups = { main, alts }, Places = { new SavedPlace { PlaceId = 920587237, Name = "Adopt Me!" }, new SavedPlace { PlaceId = 189707, Name = "Natural Disaster Survival" }, new SavedPlace { PlaceId = 142823291, Name = "Murder Mystery 2" } } };
        string[] names = { "BluePanda", "xX_Nova_Xx", "FarmAlt01", "FarmAlt02", "FarmAlt03", "Kiwi_Soda", "NightOwl77" };
        for (int i = 0; i < names.Length; i++)
            data.Accounts.Add(new Account
            {
                Username = names[i], DisplayName = names[i].Replace("_", " "), UserId = 1000 + i, SortOrder = i, Robux = i * 1370,
                GroupId = i < 2 ? main.Id : i < 5 ? alts.Id : null, Cookie = "_|WARNING:-DO-NOT-SHARE-THIS demo", LastUsed = DateTime.Now.AddHours(-i * 5),
                Alias = i == 0 ? "Main" : "", CookieValid = i != 5,
            });
        // Profils factices pour les captures d'écran
        var ids = data.Accounts.Select(a => a.Id).ToList();
        data.Profiles.Add(new LaunchProfile { Name = "Farm du soir", AccountIds = ids.Take(3).ToList(), Mode = JoinMode.Place, PlaceId = 920587237, TileAfter = true, ScheduleEnabled = true, ScheduleTime = "18:30", ScheduleDays = 31 });
        data.Profiles.Add(new LaunchProfile { Name = "Tous les alts", AccountIds = ids.Skip(2).Take(3).ToList(), Mode = JoinMode.Place, PlaceId = 189707, RamLimitMb = 1536 });
        data.Profiles.Add(new LaunchProfile { Name = "Comptes principaux", AccountIds = ids.Take(2).ToList(), Mode = JoinMode.Home });
        return data;
    }

    private static void SeedDemoRuntime(AppState app)
    {
        int n = 0;
        foreach (var a in app.Accounts.Take(3))
        {
            a.IsRunning = true; a.Pid = 4000 + n * 111; a.StartedAt = DateTime.Now.AddMinutes(-(n + 1) * 7);
            a.RamBytes = (900 + n * 420) * 1024L * 1024L; a.RamRatio = 0.25 + n * 0.2; a.AfkText = n == 2 ? "Anti-AFK désactivé pour ce compte" : $"Anti-AFK dans {4 - n}m 12s"; if (n == 2) a.AntiAfk = false; n++;
        }
        app.RefreshStats();
    }

    public static void ApplyAccent(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Current.Resources["AccentBrush"] = new SolidColorBrush(c);
            Current.Resources["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        }
        catch { }
    }
}
