using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class ProfilesPage : UserControl
{
    private readonly AppState _app;

    public ProfilesPage(AppState app)
    {
        InitializeComponent();
        _app = app;
        List.ItemsSource = app.Profiles;
        ((INotifyCollectionChanged)app.Profiles).CollectionChanged += (_, _) => UpdateEmpty();
        // les noms de comptes / jeux peuvent changer : on rafraîchit les textes quand la liste des comptes bouge
        ((INotifyCollectionChanged)app.Accounts).CollectionChanged += (_, _) => app.RefreshProfiles();
        app.GroupsChanged += () => app.RefreshProfiles();
        UpdateEmpty();
    }

    private void UpdateEmpty() => EmptyState.Visibility = _app.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static LaunchProfile? P(object sender) => (sender as FrameworkElement)?.DataContext as LaunchProfile;

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var d = new ProfileDialog(_app) { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() != true) return;
        _app.Profiles.Add(d.Result);
        _app.RefreshProfiles(); _app.RequestSave();
        _app.Status($"Profil « {d.Result.Name} » créé.");
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (P(sender) is not { } p) return;
        var d = new ProfileDialog(_app, p) { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() == true) { _app.RefreshProfiles(); _app.RequestSave(); }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
        if (List.SelectedItem is LaunchProfile p)
        {
            var dlg = new ProfileDialog(_app, p) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) { _app.RefreshProfiles(); _app.RequestSave(); }
        }
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (P(sender) is not { } p) return;
        var copy = new LaunchProfile
        {
            Name = p.Name + " (copie)", AccountIds = p.AccountIds.ToList(), Mode = p.Mode, PlaceId = p.PlaceId, Value = p.Value,
            RamLimitMb = p.RamLimitMb, TileAfter = p.TileAfter, ScheduleEnabled = false, ScheduleTime = p.ScheduleTime, ScheduleDays = p.ScheduleDays,
        };
        _app.Profiles.Add(copy);
        _app.RefreshProfiles(); _app.RequestSave();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (P(sender) is not { } p) return;
        if (!MessageDialog.Show(Window.GetWindow(this), "Supprimer le profil ?",
                $"« {p.Name} » sera supprimé. Tes comptes ne sont pas touchés.", "Supprimer", true, true)) return;
        _app.DeleteProfile(p);
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (P(sender) is not { } p || p.IsRunning) return;
        try { await _app.Instances.LaunchProfileAsync(p); }
        catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "Lancement impossible", ex.Message); }
    }
}
