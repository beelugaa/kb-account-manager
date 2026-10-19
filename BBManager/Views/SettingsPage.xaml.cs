using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BBManager.Core;
using BBManager.Models;
using BBManager.Services;
using Microsoft.Win32;

namespace BBManager.Views;

public partial class SettingsPage : UserControl
{
    private sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

    private readonly AppState _app;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _ready;

    private AppSettings S => _app.Settings;

    public SettingsPage(AppState app)
    {
        InitializeComponent();
        _app = app;
        DataContext = app.Settings;

        RamModeBox.ItemsSource = new[]
        {
            new Choice<RamMode>(RamMode.HardWorkingSet, "Plafond permanent (recommandé)"),
            new Choice<RamMode>(RamMode.Trim, "Léger (reprend la RAM par à-coups)"),
            new Choice<RamMode>(RamMode.JobObject, "Strict (peut faire planter Roblox)"),
        };
        RamModeBox.SelectedValue = S.RamMode;

        PriorityBox.ItemsSource = new[]
        {
            new Choice<CpuPriority>(CpuPriority.Normal, "Normale"),
            new Choice<CpuPriority>(CpuPriority.BelowNormal, "Inférieure à la normale"),
            new Choice<CpuPriority>(CpuPriority.Idle, "Basse"),
            new Choice<CpuPriority>(CpuPriority.AboveNormal, "Supérieure à la normale"),
        };
        PriorityBox.SelectedValue = S.Priority;

        var mons = InstanceManager.GetMonitors();
        MonitorBox.ItemsSource = mons.Select((m, i) => new Choice<int>(i, $"Écran {i + 1} — {m.Right - m.Left}×{m.Bottom - m.Top}")).ToList();
        MonitorBox.SelectedValue = Math.Clamp(S.TileMonitor, 0, Math.Max(0, mons.Count - 1));

        FlagList.ItemsSource = S.CustomFlags;
        CoresInfo.Text = $"Ton PC a {Environment.ProcessorCount} cœurs logiques. 0 = tous partagés ; sinon chaque instance reçoit son propre bloc de cœurs.";

        UpdateRamModeInfo();
        RefreshUpdateUi();
        app.Updates.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshUpdateUi);
        UpdateCrypto();
        BuildAccent();
        UpdateClientInfo();

        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Touch()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelection));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource is CheckBox) Touch(); }));

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Commit();
        };
        // Les liaisons déclenchent des TextChanged à l'affichage : on n'écoute qu'une fois la page stabilisée.
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => _ready = true, DispatcherPriority.ContextIdle);
    }

    private void Touch()
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private readonly DispatcherTimer _savedTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void Commit()
    {
        _app.RequestSave();
        TryApplyFlags(silent: true);
        _app.Instances.ApplyAll();

        SaveInfo.Text = "✓ Enregistré";
        _savedTimer.Stop();
        _savedTimer.Tick -= ClearSaved;
        _savedTimer.Tick += ClearSaved;
        _savedTimer.Start();
    }

    private void ClearSaved(object? s, EventArgs e) { _savedTimer.Stop(); SaveInfo.Text = ""; }

    private void OnJump(object sender, RoutedEventArgs e)
    {
        if (FindName((string)((Button)sender).Tag) is not FrameworkElement target) return;
        var y = target.TransformToAncestor(Content).Transform(new Point(0, 0)).Y;
        Scroller.ScrollToVerticalOffset(Math.Max(0, y - 4));
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.OriginalSource is not ComboBox cb) return;
        if (cb == RamModeBox && cb.SelectedValue is RamMode rm) { S.RamMode = rm; UpdateRamModeInfo(); }
        else if (cb == PriorityBox && cb.SelectedValue is CpuPriority cp) S.Priority = cp;
        else if (cb == MonitorBox && cb.SelectedValue is int mi) S.TileMonitor = mi;
        Touch();
    }

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Startup.Set(S.StartWithWindows);
            StartupInfo.Text = S.StartWithWindows ? "KB Manager se lancera à l'ouverture de ta session Windows." : "Démarrage automatique désactivé.";
        }
        catch (Exception ex)
        {
            S.StartWithWindows = Startup.IsEnabled();
            Rebind();
            StartupInfo.Text = "Impossible de modifier le démarrage automatique : " + ex.Message;
        }
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Quit();

    // ---- Mises à jour
    private void RefreshUpdateUi()
    {
        var u = _app.Updates;
        VersionInfo.Text = $"Version installée : {UpdateService.CurrentText}";
        UpdateStatus.Text = (string.IsNullOrEmpty(u.Status) ? "" : u.Status) + (u.LastCheck != null ? $"   (dernière vérification : {u.LastCheckText})" : "");
        InstallUpdateBtn.Visibility = u.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateBtn.Content = u.HasUpdate ? $"Installer la version {u.Available!.VersionText}" : "Installer la mise à jour";
        CheckUpdateBtn.IsEnabled = !u.Busy && u.IsConfigured;
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await _app.Updates.CheckAsync();
        RefreshUpdateUi();
    }

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ShowUpdateDialog();

    private void UpdateRamModeInfo()
    {
        RamModeInfo.Text = S.RamMode switch
        {
            RamMode.HardWorkingSet => "Windows interdit à la fenêtre de dépasser la limite en RAM physique (le surplus passe sur le disque). La limite tient en permanence. En dessous d'environ 1 Go, Roblox peut saccader.",
            RamMode.Trim => "Toutes les quelques secondes, l'appli vide la RAM d'une fenêtre qui dépasse la limite. Roblox la regonfle vite : la limite est très approximative.",
            _ => "Plafond sur la mémoire réservée (commit), imposé par Windows. Si Roblox le dépasse, il plante — réservé aux tests.",
        };
    }

    // ---- Multi-instance -----------------------------------------------------
    private void OnMultiClick(object sender, RoutedEventArgs e)
    {
        if (S.MultiInstance)
        {
            if (!MultiInstance.Enable())
                MessageDialog.Show(Window.GetWindow(this), "Roblox est déjà ouvert",
                    "Un client Roblox lancé hors de l'appli détient le verrou. Ferme tous les Roblox (onglet Instances), puis relance KB Manager.");
        }
        else MultiInstance.Disable();
        (Application.Current.MainWindow as MainWindow)?.UpdateMultiCard();
    }

    // ---- FPS / flags --------------------------------------------------------
    private void OnFpsPreset(object sender, RoutedEventArgs e)
    {
        S.FpsCap = int.Parse((string)((Button)sender).Tag);
        S.UnlockFps = true;
        Rebind();
        Touch();
    }

    private void OnRamPreset(object sender, RoutedEventArgs e)
    {
        S.DefaultRamLimitMb = int.Parse((string)((Button)sender).Tag);
        Rebind();
        Touch();
    }

    private void Rebind()
    {
        bool was = _ready;
        _ready = false;
        DataContext = null; DataContext = S;
        RamModeBox.SelectedValue = S.RamMode;
        PriorityBox.SelectedValue = S.Priority;
        _ready = was;
    }

    private void OnAddFlag(object sender, RoutedEventArgs e)
    {
        S.CustomFlags.Add(new FlagEntry { Key = "", Value = "" });
        Touch();
    }

    private void OnRemoveFlag(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FlagEntry f) { S.CustomFlags.Remove(f); Touch(); }
    }

    private bool TryApplyFlags(bool silent)
    {
        var exe = RobloxClient.FindPlayerExe(S);
        if (exe == null)
        {
            if (!silent) _app.Status("Client Roblox introuvable : indique son chemin plus bas.", true);
            UpdateClientInfo();
            return false;
        }
        try
        {
            var file = RobloxClient.ApplyFlags(S, exe);
            if (!silent) _app.Status("Réglages du client écrits dans " + file);
            UpdateClientInfo();
            return true;
        }
        catch (Exception ex)
        {
            if (!silent) _app.Status("Écriture des flags impossible : " + ex.Message, true);
            return false;
        }
    }

    private void OnApplyFlags(object sender, RoutedEventArgs e)
    {
        TryApplyFlags(silent: false);
        _app.RequestSave();
    }

    private void OnClearFlags(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "Retirer tous les flags ?",
                "Le déblocage des FPS, les graphismes minimum et tous les flags personnalisés seront désactivés et retirés du client.", "Retirer", true, true)) return;
        S.UnlockFps = false; S.LowGraphics = false; S.CustomFlags.Clear();
        Rebind();
        TryApplyFlags(silent: false);
        _app.RequestSave();
    }

    private void OnOpenClientDir(object sender, RoutedEventArgs e)
    {
        var exe = RobloxClient.FindPlayerExe(S);
        if (exe == null) { _app.Status("Client Roblox introuvable.", true); return; }
        Process.Start("explorer.exe", System.IO.Path.GetDirectoryName(exe)!);
    }

    private void UpdateClientInfo()
    {
        var exe = RobloxClient.FindPlayerExe(S);
        ClientInfo.Text = exe == null
            ? "Client Roblox : introuvable (installe Roblox ou renseigne le chemin dans « Lancement et fenêtres »)."
            : $"Client détecté : {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(exe))}  —  {exe}";
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "RobloxPlayerBeta.exe|RobloxPlayerBeta.exe|Exécutables|*.exe", Title = "Choisir RobloxPlayerBeta.exe" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            S.RobloxExePath = dlg.FileName;
            Rebind(); Touch(); UpdateClientInfo();
        }
    }

    private void OnApplyRunning(object sender, RoutedEventArgs e)
    {
        _app.Instances.ApplyAll();
        _app.Status("Réglages appliqués aux instances en cours.");
    }

    // ---- Sécurité -----------------------------------------------------------
    private void UpdateCrypto()
    {
        bool pwd = DataStore.HasPassword;
        CryptoInfo.Text = pwd
            ? "🔒  Mot de passe maître actif — chiffrement AES-256-GCM."
            : "🔐  Chiffrement Windows (DPAPI) : lié à ta session Windows, sans mot de passe à saisir.";
        SetPwdBtn.Content = pwd ? "Changer le mot de passe" : "Définir un mot de passe maître";
        RemovePwdBtn.Visibility = pwd ? Visibility.Visible : Visibility.Collapsed;
        S.Encrypted = pwd;
    }

    private void OnSetPassword(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        // Changer un mot de passe existant demande d'abord l'ancien
        if (DataStore.HasPassword && !Reauth.Require(owner, "changer le mot de passe")) return;
        var d = new PasswordDialog(PasswordDialog.PasswordMode.Set) { Owner = owner };
        if (d.ShowDialog() != true) return;
        DataStore.SetPassword(d.Password);
        _app.SaveNow();
        Reauth.Forget();
        UpdateCrypto();
        _app.Status("Mot de passe enregistré. Il te sera demandé à chaque démarrage.");
    }

    private void OnRemovePassword(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (!Reauth.Require(owner, "retirer le mot de passe")) return;
        if (!MessageDialog.Show(owner, "Retirer le mot de passe ?",
                "Les données repasseront sur le chiffrement Windows (sans mot de passe).", "Retirer", true)) return;
        DataStore.SetPassword(null);
        _app.SaveNow();
        UpdateCrypto();
        _app.Status("Mot de passe retiré.");
    }

    private void OnLockNow(object sender, RoutedEventArgs e)
    {
        if (!DataStore.HasPassword) { _app.Status("Définis d'abord un mot de passe pour pouvoir verrouiller l'appli.", true); return; }
        (Application.Current.MainWindow as MainWindow)?.Lock();
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ApplyCaptureExclusion();

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (_app.Accounts.Count == 0) { _app.Status("Aucun compte à exporter.", true); return; }
        if (!Reauth.Require(owner, "exporter les cookies de tous les comptes")) return;

        var pd = new PasswordDialog(PasswordDialog.PasswordMode.Set, null, "Mot de passe de l'export",
            "Ce mot de passe chiffre le fichier exporté. Tu en auras besoin pour le réimporter.") { Owner = owner };
        if (pd.ShowDialog() != true) return;

        var dlg = new SaveFileDialog { Filter = "Export KB Manager chiffré|*.kbx", FileName = "kb-comptes.kbx" };
        if (dlg.ShowDialog(owner) != true) return;

        var plain = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", _app.Accounts.Select(a => $"{a.Username}:{a.Cookie}")));
        try { File.WriteAllBytes(dlg.FileName, DataStore.EncryptBlob(plain, pd.Password)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        _app.Status($"{_app.Accounts.Count} compte(s) exporté(s) (chiffré) dans {dlg.FileName}");
    }

    private void OnResetApp(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (!Reauth.Require(owner, "réinitialiser l'appli")) return;
        if (!MessageDialog.Show(owner, "Réinitialiser l'appli ?",
                "Tous les comptes, groupes, jeux et réglages enregistrés seront supprimés définitivement, puis l'appli redémarrera comme au premier lancement. " +
                "Tes Roblox ouverts ne sont pas fermés.", "Réinitialiser", true, true)) return;
        App.ResetAndRestart();
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", Util.AppDir);

    // ---- Apparence ----------------------------------------------------------
    private void BuildAccent()
    {
        AccentPanel.Children.Clear();
        foreach (var hex in GroupDialog.Palette.Take(8))
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var el = new Ellipse
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), Fill = new SolidColorBrush(c), Cursor = System.Windows.Input.Cursors.Hand,
                Stroke = string.Equals(hex, S.Accent, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent, StrokeThickness = 2.5,
            };
            el.MouseLeftButtonUp += (_, _) =>
            {
                S.Accent = hex;
                App.ApplyAccent(hex);
                _app.RequestSave();
                BuildAccent();
            };
            AccentPanel.Children.Add(el);
        }
    }
}
