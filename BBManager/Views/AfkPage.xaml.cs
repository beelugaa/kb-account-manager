using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class AfkPage : UserControl
{
    private sealed record MethodChoice(AfkMethod? Value, string Label) { public override string ToString() => Label; }

    private readonly AppState _app;
    private readonly ListCollectionView _view;
    private bool _ready;
    private readonly DispatcherTimer _clearTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private AppSettings S => _app.Settings;

    public AfkPage(AppState app)
    {
        InitializeComponent();
        _app = app;

        _view = (ListCollectionView)new CollectionViewSource { Source = app.Accounts }.View;
        _view.Filter = o => ShowAllBox?.IsChecked == true || ((Account)o).IsRunning;
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(Account.IsRunning));
        _view.IsLiveSorting = true;
        _view.LiveSortingProperties.Add(nameof(Account.RunSort));
        _view.LiveSortingProperties.Add(nameof(Account.SortKey));
        _view.SortDescriptions.Add(new SortDescription(nameof(Account.RunSort), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(Account.SortKey), ListSortDirection.Ascending));
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateSummary();
        List.ItemsSource = _view;

        MasterBox.IsChecked = S.AfkEnabled;
        GlobalIntervalBox.Text = S.AfkIntervalSec.ToString();
        GlobalGateBox.Text = S.AfkIdleGateSec.ToString();
        GlobalGapBox.Text = S.AfkGapSec.ToString();
        GlobalMethodBox.ItemsSource = new[]
        {
            new MethodChoice(AfkMethod.Jump, "Saut (touche Espace)"),
            new MethodChoice(AfkMethod.Discreet, "Discret (touche F15)"),
        };
        GlobalMethodBox.SelectedIndex = (int)S.AfkMethod;

        BulkMethodBox.ItemsSource = new[]
        {
            new MethodChoice(null, "Méthode par défaut"),
            new MethodChoice(AfkMethod.Jump, "Saut (touche Espace)"),
            new MethodChoice(AfkMethod.Discreet, "Discret (touche F15)"),
        };
        BulkMethodBox.SelectedIndex = 0;
        BulkInterval.Text = "0";

        _clearTimer.Tick += (_, _) => { _clearTimer.Stop(); ActionResult.Text = ""; };
        app.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.StatsText)) UpdateSummary(); };

        _ready = true;
        UpdateSummary();
        OnSelectionChanged(this, null!);
    }

    private List<Account> Selected() => List.SelectedItems.Cast<Account>().ToList();

    private void Say(string msg)
    {
        ActionResult.Text = msg;
        _clearTimer.Stop(); _clearTimer.Start();
    }

    private void UpdateSummary()
    {
        if (Summary == null) return;
        var running = _app.Accounts.Where(a => a.IsRunning).ToList();
        int on = running.Count(a => a.AntiAfk);
        Summary.Text = !S.AfkEnabled
            ? "Interrupteur général désactivé : aucun anti-AFK n'est envoyé."
            : $"{running.Count} compte(s) en jeu · anti-AFK actif sur {on}";
        EmptyState.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshConfigTexts()
    {
        foreach (var a in _app.Accounts) a.RefreshAfkConfig();
    }

    // ---- Réglages globaux ---------------------------------------------------
    private void OnGlobalChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        S.AfkEnabled = MasterBox.IsChecked == true;
        _app.RequestSave();
        UpdateSummary();
    }

    private void OnGlobalMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || GlobalMethodBox.SelectedItem is not MethodChoice { Value: { } m }) return;
        S.AfkMethod = m;
        _app.RequestSave();
        RefreshConfigTexts();
    }

    private void OnGlobalIntervalChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || !int.TryParse(GlobalIntervalBox.Text.Trim(), out var v) || v < 15) return;
        S.AfkIntervalSec = v;
        foreach (var a in _app.Accounts) a.NextAfkAt = null;   // replanifie avec le nouvel intervalle
        _app.RequestSave();
        RefreshConfigTexts();
    }

    private void OnGlobalGapChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || !int.TryParse(GlobalGapBox.Text.Trim(), out var v) || v < 5 || v > 600) return;
        S.AfkGapSec = v;
        foreach (var a in _app.Accounts) a.NextAfkAt = null;      // replanifie avec le nouvel écart
        _app.RequestSave();
    }

    private void OnGlobalGateChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || !int.TryParse(GlobalGateBox.Text.Trim(), out var v) || v < 2 || v > 600) return;
        S.AfkIdleGateSec = v;
        _app.RequestSave();
    }

    // ---- Liste --------------------------------------------------------------
    private void OnShowAll(object sender, RoutedEventArgs e) { _view.Refresh(); UpdateSummary(); }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = List.SelectedItems.Count;
        SelText.Text = n == 0 ? "Sélectionne un ou plusieurs comptes pour les gérer" : $"{n} sélectionné(s)";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => List.SelectAll();
    private void OnSelectNone(object sender, RoutedEventArgs e) => List.UnselectAll();

    private static Account? Acc(object sender) => (sender as FrameworkElement)?.DataContext as Account;

    private void OnRowToggle(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is not { } a) return;
        a.NextAfkAt = null;
        a.RefreshAfkConfig();
        _app.RequestSave();
        UpdateSummary();
    }

    private async void OnRowSend(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is not { } a) return;
        if (!a.IsRunning) { Say($"{a.Title} n'est pas en jeu."); return; }
        var (ok, total) = await _app.Instances.SendAfkNowAsync(new[] { a });
        Say(ok == total ? $"AFK envoyé à {a.Title}." : $"Échec pour {a.Title} (fenêtre introuvable ?).");
    }

    // ---- Actions sur la sélection ------------------------------------------
    private void SetEnabled(bool on)
    {
        var sel = Selected();
        if (sel.Count == 0) { Say("Sélectionne d'abord au moins un compte."); return; }
        foreach (var a in sel) { a.AntiAfk = on; a.NextAfkAt = null; a.RefreshAfkConfig(); }
        _app.RequestSave();
        UpdateSummary();
        Say($"Anti-AFK {(on ? "activé" : "désactivé")} pour {sel.Count} compte(s).");
    }

    private void OnEnable(object sender, RoutedEventArgs e) => SetEnabled(true);
    private void OnDisable(object sender, RoutedEventArgs e) => SetEnabled(false);

    private void OnApplyInterval(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { Say("Sélectionne d'abord au moins un compte."); return; }
        if (!int.TryParse(BulkInterval.Text.Trim(), out var v) || v < 0 || (v > 0 && v < 15)) { Say("Intervalle invalide (0 = par défaut, sinon 15 s minimum)."); return; }
        if (v > 1200 && !MessageDialog.Show(Window.GetWindow(this), "Intervalle trop long",
                "Au-delà de 20 minutes (1200 s), Roblox peut expulser le compte avant l'envoi. Continuer quand même ?", "Continuer", true)) return;
        foreach (var a in sel) { a.AfkIntervalSec = v; a.NextAfkAt = null; a.RefreshAfkConfig(); }
        _app.RequestSave();
        Say(v == 0 ? $"{sel.Count} compte(s) : intervalle par défaut." : $"{sel.Count} compte(s) : toutes les {v} s.");
    }

    private void OnApplyMethod(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { Say("Sélectionne d'abord au moins un compte."); return; }
        var m = (BulkMethodBox.SelectedItem as MethodChoice)?.Value;
        foreach (var a in sel) { a.AfkMethodOverride = m; a.RefreshAfkConfig(); }
        _app.RequestSave();
        Say($"Méthode appliquée à {sel.Count} compte(s).");
    }

    private async void OnSendNow(object sender, RoutedEventArgs e)
    {
        var sel = Selected().Where(a => a.IsRunning).ToList();
        if (sel.Count == 0) { Say("Sélectionne au moins un compte en jeu."); return; }
        Say($"Envoi à {sel.Count} fenêtre(s)…");
        var (ok, total) = await _app.Instances.SendAfkNowAsync(sel);
        Say($"Envoyé à {ok}/{total} fenêtre(s).");
    }
}
