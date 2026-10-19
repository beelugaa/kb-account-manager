using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BBManager.Core;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class InstancesPage : UserControl
{
    private readonly AppState _app;
    private readonly ListCollectionView _view;

    public InstancesPage(AppState app)
    {
        InitializeComponent();
        _app = app;

        _view = (ListCollectionView)new CollectionViewSource { Source = app.Accounts }.View;
        _view.Filter = o => ((Account)o).IsRunning;
        _view.SortDescriptions.Add(new SortDescription(nameof(Account.StartedAt), ListSortDirection.Ascending));
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(Account.IsRunning));
        _view.IsLiveSorting = true;
        _view.LiveSortingProperties.Add(nameof(Account.StartedAt));
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => Update();
        List.ItemsSource = _view;

        app.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.StatsText)) Update(); };
        Update();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    private void Update()
    {
        int n = _view.Count;
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        long used = _app.Accounts.Where(a => a.IsRunning).Sum(a => a.RamBytes);
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref ms);
        double total = ms.ullTotalPhys;
        int unmanaged = _app.Instances.CountUnmanaged();
        Summary.Text = (n == 0 ? "Rien en cours" : $"{n} instance(s) active(s)")
                       + (unmanaged > 0 ? $"  ·  {unmanaged} Roblox non reconnu(s) (lancés hors de l'appli)" : "");
        SysText.Text = $"Roblox : {Util.FormatBytes(used)}   ·   RAM système : {Util.FormatBytes((long)(ms.ullTotalPhys - ms.ullAvailPhys))} / {Util.FormatBytes((long)total)} ({ms.dwMemoryLoad} %)";
        SysBar.Value = total > 0 ? Math.Min(1, (total - ms.ullAvailPhys) / total) : 0;
    }

    private static Account? Acc(object sender) => (sender as FrameworkElement)?.DataContext as Account;

    private void OnTile(object sender, RoutedEventArgs e) => _app.Instances.TileWindows();

    private void OnApply(object sender, RoutedEventArgs e)
    {
        _app.Instances.ApplyAll();
        _app.Status("Priorité, cœurs et limites RAM ré-appliqués.");
    }

    private void OnTrim(object sender, RoutedEventArgs e)
    {
        int n = _app.Instances.TrimAllNow();
        _app.Status($"RAM libérée sur {n} instance(s).");
    }

    private void OnCloseAll(object sender, RoutedEventArgs e)
    {
        if (_view.Count == 0) return;
        if (!MessageDialog.Show(Window.GetWindow(this), "Tout fermer ?", $"Fermer les {_view.Count} instance(s) lancées par l'appli ?", "Fermer", true, true)) return;
        _app.Instances.CloseAll();
        _app.Status("Toutes les instances ont été fermées.");
    }

    private void OnKillAllRoblox(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "Fermer tous les Roblox ?",
                "Tous les clients Roblox ouverts sur le PC vont être fermés, y compris ceux lancés en dehors de l'appli. " +
                "Fais-le avant d'activer le multi-instance (relance ensuite l'appli).", "Tout fermer", true, true)) return;
        _app.Instances.CloseAll();
        int n = RobloxClient.KillAll();
        _app.Status($"{n} processus Roblox terminé(s).");
    }

    private void OnFocus(object sender, RoutedEventArgs e) { if (Acc(sender) is { } a) _app.Instances.Focus(a); }

    private async void OnRelaunch(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is not { } a || a.LastLaunch == null) { _app.Status("Pas de lancement précédent à rejouer.", true); return; }
        try { await _app.Instances.LaunchAsync(a, a.LastLaunch); }
        catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "Relance impossible", ex.Message); }
    }

    private void OnClose(object sender, RoutedEventArgs e) { if (Acc(sender) is { } a) _app.Instances.Close(a); }

    private void OnLimitChanged(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is not { } a) return;
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (a.RamLimitMb < 0) a.RamLimitMb = 0;
        _app.Instances.ApplyProcessSettings(a);
        _app.RequestSave();
    }

    private void OnLimitKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnLimitChanged(sender, e);
        Keyboard.ClearFocus();
    }
}
