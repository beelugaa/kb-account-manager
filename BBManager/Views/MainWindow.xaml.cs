using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using BBManager.Services;

namespace BBManager.Views;

public partial class MainWindow : Window
{
    private readonly AppState _app;

    public MainWindow(AppState app)
    {
        InitializeComponent();
        _app = app;
        DataContext = app;

        AccountsHost.Content = new AccountsPage(app);
        ProfilesHost.Content = new ProfilesPage(app);
        InstancesHost.Content = new InstancesPage(app);
        AfkHost.Content = new AfkPage(app);
        SettingsHost.Content = new SettingsPage(app);

        app.PropertyChanged += OnAppChanged;
        app.Updates.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(UpdateService.HasUpdate) or nameof(UpdateService.BadgeText)) RefreshUpdateCard(); };
        RefreshUpdateCard();
        UpdateStatusColor();
        UpdateMultiCard();
        app.RefreshStats();

        Closed += (_, _) => Application.Current.Shutdown();

        // Verrouillage : activité dans l'appli, minuteur d'inactivité, Ctrl+L
        PreviewMouseMove += (_, _) => _lastInput = DateTime.Now;
        PreviewMouseDown += (_, _) => _lastInput = DateTime.Now;
        PreviewKeyDown += (_, e) =>
        {
            _lastInput = DateTime.Now;
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control && DataStore.HasPassword) { Lock(); e.Handled = true; }
        };
        _lockTimer.Tick += (_, _) =>
        {
            var minutes = _app.Settings.AutoLockMinutes;
            if (!_locked && DataStore.HasPassword && minutes > 0 && IsVisible && DateTime.Now - _lastInput >= TimeSpan.FromMinutes(minutes)) Lock();
        };
        _lockTimer.Start();
        _lockWait.Tick += (_, _) => UpdateLockWait();
        IsVisibleChanged += (_, _) => { if (IsVisible && _locked) LockPwd.Focus(); };
        SourceInitialized += (_, _) => ApplyCaptureExclusion();
    }

    // =====================================================================
    //  Mises à jour
    // =====================================================================
    private void RefreshUpdateCard()
    {
        UpdateCard.Visibility = _app.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardText.Text = _app.Updates.BadgeText;
    }

    private void OnUpdateCardClick(object sender, MouseButtonEventArgs e) => ShowUpdateDialog();

    public void ShowUpdateDialog()
    {
        if (_locked || _app.Updates.Available is not { } info) return;
        new UpdateDialog(_app, info) { Owner = this }.ShowDialog();
    }

    // =====================================================================
    //  Verrouillage
    // =====================================================================
    private bool _locked;
    private DateTime _lastInput = DateTime.Now;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _lockWait = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public bool IsLocked => _locked;

    public void ApplyCaptureExclusion()
        => ProcessHardening.SetCaptureExclusion(new WindowInteropHelper(this).Handle, _app.Settings.HideFromCapture);

    /// <summary>Verrouille l'interface (ne fait rien sans mot de passe). Les instances, l'anti-AFK et le reste continuent de tourner.</summary>
    public void Lock()
    {
        if (!DataStore.HasPassword || _locked) return;
        _locked = true;
        Reauth.Forget();
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();       // pas de boîte de dialogue ouverte derrière le verrou
        foreach (UIElement child in ((Grid)Content).Children) if (child != LockLayer) child.IsEnabled = false;
        LockPwd.Clear(); LockErr.Visibility = Visibility.Collapsed;
        LockLayer.Visibility = Visibility.Visible;
        if (IsVisible) LockPwd.Focus();
        UpdateLockWait();
    }

    private void Unlock()
    {
        _locked = false;
        LockLayer.Visibility = Visibility.Collapsed;
        foreach (UIElement child in ((Grid)Content).Children) child.IsEnabled = true;
        LockPwd.Clear();
        _lastInput = DateTime.Now;
    }

    private void UpdateLockWait()
    {
        var left = AttemptGuard.Remaining;
        if (left > TimeSpan.Zero)
        {
            LockBtn.IsEnabled = false;
            LockErr.Text = $"Trop d'essais. Réessaie dans {Math.Ceiling(left.TotalSeconds)} s.";
            LockErr.Visibility = Visibility.Visible;
            _lockWait.Start();
        }
        else
        {
            if (!LockBtn.IsEnabled) { LockBtn.IsEnabled = true; LockErr.Visibility = Visibility.Collapsed; }
            _lockWait.Stop();
        }
    }

    private void OnLockKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnLockUnlock(sender, e); }

    private void OnLockUnlock(object sender, RoutedEventArgs e)
    {
        if (!LockBtn.IsEnabled || LockPwd.Password.Length == 0) return;
        if (DataStore.VerifyPassword(LockPwd.Password)) { AttemptGuard.Success(); Unlock(); return; }
        AttemptGuard.Failure();
        LockPwd.Clear(); LockPwd.Focus();
        LockErr.Text = "Mot de passe incorrect."; LockErr.Visibility = Visibility.Visible;
        UpdateLockWait();
    }

    private void OnLockForgot(object sender, RoutedEventArgs e)
    {
        if (ForgotPasswordDialog.Ask(this)) App.ResetAndRestart();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Exiting && _app.Settings.CloseToTray && App.Tray != null)
        {
            e.Cancel = true;
            if (_app.Settings.LockOnTray) Lock();
            Hide();
            App.Tray.NotifyHidden();
            _app.SaveNow();
        }
        base.OnClosing(e);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.StatusIsError) or nameof(AppState.StatusMessage)) UpdateStatusColor();
        if (e.PropertyName is nameof(AppState.RunningCount) or "MultiState") UpdateMultiCard();
    }

    private void UpdateStatusColor()
    {
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(_app.StatusIsError ? "BadBrush" : "DimBrush");
    }

    public void UpdateMultiCard()
    {
        bool on = MultiInstance.IsActive || _app.ReadOnly;   // mode démo : affichage « actif »
        if (!_app.Settings.MultiInstance)
        {
            MultiDot.Fill = (System.Windows.Media.Brush)FindResource("FaintBrush");
            MultiText.Text = "Désactivé dans les paramètres.";
            return;
        }
        if (on)
        {
            MultiDot.Fill = (System.Windows.Media.Brush)FindResource("GoodBrush");
            MultiText.Text = "Actif — tu peux lancer autant d'instances que tu veux.";
            return;
        }

        // Bloqué : on dit précisément pourquoi, et ça se débloque tout seul (nouvel essai toutes les ~8 s)
        MultiDot.Fill = (System.Windows.Media.Brush)FindResource("WarnBrush");
        int me = Environment.ProcessId;
        bool otherManager = System.Diagnostics.Process.GetProcessesByName("BBManager").Concat(System.Diagnostics.Process.GetProcessesByName("KBManager"))
            .Any(p => p.Id != me);
        MultiText.Text = otherManager
            ? "Une autre fenêtre de KB/BB Manager est ouverte. Ferme-la : le multi-instance s'active tout seul."
            : "Un Roblox lancé hors de l'appli est ouvert. Ferme-le : le multi-instance s'active tout seul.";
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (AccountsHost == null) return;
        var tag = (sender as RadioButton)?.Tag as string;
        AccountsHost.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesHost.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        InstancesHost.Visibility = tag == "instances" ? Visibility.Visible : Visibility.Collapsed;
        AfkHost.Visibility = tag == "afk" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
}
