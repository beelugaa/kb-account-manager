using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class AccountsPage : UserControl
{
    private sealed record SortMode(string Label, string Prop, ListSortDirection Dir, string? Then = null) { public override string ToString() => Label; }
    private sealed record ModeItem(JoinMode Value, string Label) { public override string ToString() => Label; }

    private static readonly SortMode[] SortModes =
    {
        new("Ordre manuel (glisser-déposer)", "SortOrder", ListSortDirection.Ascending),
        new("Nom (A → Z)", "SortKey", ListSortDirection.Ascending),
        new("Nom (Z → A)", "SortKey", ListSortDirection.Descending),
        new("Dernière utilisation", "LastUsed", ListSortDirection.Descending),
        new("En jeu d'abord", "RunSort", ListSortDirection.Ascending, "SortKey"),
        new("Robux (décroissant)", "Robux", ListSortDirection.Descending),
        new("Ajout récent", "AddedAt", ListSortDirection.Descending),
    };

    private readonly AppState _app;
    private readonly ListCollectionView _view;
    private Guid? _filterGroup;
    private bool _filterNone;
    private bool _loading = true;
    private CancellationTokenSource? _cts;

    private Account? _menuAcc;   // ligne sur laquelle le menu a été ouvert (⋯ ou clic droit)

    private Point _dragStart;
    private Account? _dragAcc;

    public AccountsPage(AppState app)
    {
        InitializeComponent();
        _app = app;

        _view = (ListCollectionView)new CollectionViewSource { Source = app.Accounts }.View;
        _view.IsLiveSorting = true;
        foreach (var p in new[] { "SortOrder", "SortKey", "LastUsed", "RunSort", "Robux", "AddedAt", "GroupSort" }) _view.LiveSortingProperties.Add(p);
        _view.IsLiveGrouping = true;
        _view.LiveGroupingProperties.Add("GroupName");
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { "GroupName", "Title", "Username", "DisplayName" }) _view.LiveFilteringProperties.Add(p);
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateCounts();
        List.ItemsSource = _view;

        SortBox.ItemsSource = SortModes;
        SortBox.DisplayMemberPath = "Label";
        SortBox.SelectedItem = SortModes.FirstOrDefault(s => s.Label == app.Settings.SortMode) ?? SortModes[0];
        GroupToggle.IsChecked = app.Settings.GroupView;

        ModeBox.ItemsSource = new[]
        {
            new ModeItem(JoinMode.Home, "Accueil de Roblox"),
            new ModeItem(JoinMode.Place, "Rejoindre un jeu"),
            new ModeItem(JoinMode.JobId, "Serveur précis (Job ID)"),
            new ModeItem(JoinMode.PrivateServer, "Serveur privé"),
            new ModeItem(JoinMode.FollowUser, "Rejoindre un joueur"),
        };
        ModeBox.SelectedValue = app.Settings.LastJoinMode;
        ValueBox.Text = app.Settings.LastJoinValue;

        PlaceBox.ItemsSource = app.Places;
        FavList.ItemsSource = app.Places;
        app.Places.CollectionChanged += (_, _) => UpdateFavVisibility();
        UpdateFavVisibility();
        if (app.Settings.LastPlaceId != 0)
        {
            var sp = app.Places.FirstOrDefault(p => p.PlaceId == app.Settings.LastPlaceId);
            if (sp != null) PlaceBox.SelectedItem = sp; else PlaceBox.Text = app.Settings.LastPlaceId.ToString();
        }

        List.PreviewMouseRightButtonDown += (_, e) =>
        {
            var a = AccountAt(e.OriginalSource as DependencyObject);
            _menuAcc = a;
            if (a != null && !List.SelectedItems.Contains(a)) { List.SelectedItems.Clear(); List.SelectedItems.Add(a); }
        };

        app.GroupsChanged += BuildChips;
        _loading = false;
        ApplyView();
        BuildChips();
        UpdateModeUi();
        UpdateCounts();
    }

    // =====================================================================
    //  Vue : tri / filtre / regroupement
    // =====================================================================
    private void ApplyView()
    {
        if (_loading) return;
        var mode = (SortMode)SortBox.SelectedItem;
        bool grouped = GroupToggle.IsChecked == true;

        using (_view.DeferRefresh())
        {
            _view.GroupDescriptions.Clear();
            _view.SortDescriptions.Clear();
            if (grouped)
            {
                _view.GroupDescriptions.Add(new PropertyGroupDescription("GroupName"));
                _view.SortDescriptions.Add(new SortDescription("GroupSort", ListSortDirection.Ascending));
            }
            _view.SortDescriptions.Add(new SortDescription(mode.Prop, mode.Dir));
            if (mode.Then != null) _view.SortDescriptions.Add(new SortDescription(mode.Then, ListSortDirection.Ascending));
            _view.Filter = Filter;
        }
        UpdateCounts();
    }

    private bool Filter(object o)
    {
        var a = (Account)o;
        if (_filterGroup != null && a.GroupId != _filterGroup) return false;
        if (_filterNone && a.GroupId != null) return false;

        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Notes.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.UserId.ToString().Contains(q);
    }

    private void UpdateCounts()
    {
        if (CountText == null) return;
        int shown = _view.Count, total = _app.Accounts.Count;
        CountText.Text = shown == total ? $"{total} compte(s)" : $"{shown} affiché(s) sur {total}";
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total == 0)
        {
            EmptyTitle.Text = "Aucun compte pour l'instant";
            EmptyText.Text = "Clique sur « Ajouter » pour coller un cookie ou te connecter via le navigateur.";
        }
        else
        {
            EmptyTitle.Text = "Aucun résultat";
            EmptyText.Text = "Change le filtre ou la recherche.";
        }
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (!_loading) { _view.Refresh(); UpdateCounts(); } }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SortBox.SelectedItem is not SortMode m) return;
        _app.Settings.SortMode = m.Label; _app.RequestSave();
        ApplyView();
    }

    private void OnGroupToggle(object sender, RoutedEventArgs e)
    {
        _app.Settings.GroupView = GroupToggle.IsChecked == true; _app.RequestSave();
        ApplyView();
    }

    // =====================================================================
    //  Pastilles de groupes
    // =====================================================================
    private void BuildChips()
    {
        Chips.Children.Clear();
        if (_filterGroup != null && _app.Groups.All(g => g.Id != _filterGroup)) _filterGroup = null;
        if (_app.Groups.Count == 0) _filterNone = false;

        Chips.Children.Add(MakeChip("Tous", _app.Accounts.Count, null, _filterGroup == null && !_filterNone, () => SetFilter(null, false), null));
        foreach (var g in _app.Groups)
        {
            var grp = g;
            Chips.Children.Add(MakeChip(g.Name, g.Count, g.Color, _filterGroup == g.Id, () => SetFilter(grp.Id, false), grp));
        }
        if (_app.Groups.Count > 0)
        {
            int n = _app.Accounts.Count(a => a.GroupId == null);
            Chips.Children.Add(MakeChip("Sans groupe", n, "#6B7080", _filterNone, () => SetFilter(null, true), null, noneChip: true));
        }

        var add = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("LineBrush"), Background = Brushes.Transparent,
            Child = new TextBlock { Text = "＋ Groupe", Foreground = (Brush)FindResource("DimBrush") },
        };
        add.MouseLeftButtonUp += (_, _) => OnNewGroup();
        Chips.Children.Add(add);
    }

    private void SetFilter(Guid? group, bool none)
    {
        _filterGroup = group; _filterNone = none;
        _view.Refresh(); UpdateCounts(); BuildChips();
    }

    private Border MakeChip(string text, int count, string? color, bool selected, Action onClick, AccountGroup? group, bool noneChip = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (color != null)
            sp.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)new BBManager.Core.ColorBrushConverter().Convert(color, typeof(Brush), null!, null!) });
        sp.Children.Add(new TextBlock { Text = text, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal });
        sp.Children.Add(new TextBlock { Text = "  " + count, Foreground = (Brush)FindResource("DimBrush") });

        var chip = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = sp, AllowDrop = group != null || noneChip,
            Background = selected ? (Brush)FindResource("AccentSoftBrush") : (Brush)FindResource("Bg2Brush"),
            BorderBrush = selected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush"),
        };
        chip.MouseLeftButtonUp += (_, _) => onClick();

        if (chip.AllowDrop)
        {
            chip.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("bb.accounts") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            chip.Drop += (_, e) =>
            {
                if (e.Data.GetData("bb.accounts") is Account[] dropped)
                {
                    _app.SetGroup(dropped, group?.Id);
                    _app.Status($"{dropped.Length} compte(s) déplacé(s) vers « {(group?.Name ?? "Sans groupe")} ».");
                }
                e.Handled = true;
            };
        }

        if (group != null)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Lancer tout le groupe", () => _ = LaunchBatchAsync(_app.Accounts.Where(a => a.GroupId == group.Id).ToList())));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Renommer / changer la couleur…", () =>
            {
                var d = new GroupDialog(group.Name, group.Color, edit: true) { Owner = Window.GetWindow(this) };
                if (d.ShowDialog() == true) { group.Name = d.GroupName; group.Color = d.GroupColor; _app.RebuildGroupInfo(); _app.RequestSave(); }
            }));
            menu.Items.Add(Item("Déplacer vers la gauche", () => _app.MoveGroup(group, -1)));
            menu.Items.Add(Item("Déplacer vers la droite", () => _app.MoveGroup(group, +1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Supprimer le groupe", () =>
            {
                if (MessageDialog.Show(Window.GetWindow(this), "Supprimer le groupe ?",
                        $"« {group.Name} » sera supprimé. Les {group.Count} compte(s) restent dans l'appli, sans groupe.", "Supprimer", true, true))
                    _app.DeleteGroup(group);
            }));
            chip.ContextMenu = menu;
        }
        return chip;
    }

    private static MenuItem Item(string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        return mi;
    }

    private void OnNewGroup()
    {
        var d = new GroupDialog { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() == true)
        {
            var g = _app.AddGroup(d.GroupName, d.GroupColor);
            // Si des comptes sont sélectionnés, on propose de les y mettre directement
            var sel = SelectedAccounts();
            if (sel.Count > 0 && MessageDialog.Show(Window.GetWindow(this), "Ajouter la sélection ?",
                    $"Mettre les {sel.Count} compte(s) sélectionné(s) dans « {g.Name} » ?", "Oui", true))
                _app.SetGroup(sel, g.Id);
        }
    }

    // =====================================================================
    //  Ajout de comptes
    // =====================================================================
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var cm = AddBtn.ContextMenu!;
        cm.PlacementTarget = AddBtn; cm.Placement = PlacementMode.Bottom; cm.IsOpen = true;
    }

    private void OnAddCookies(object sender, RoutedEventArgs e)
    {
        new AddAccountsDialog(_app, _filterGroup) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void OnAddBrowser(object sender, RoutedEventArgs e)
    {
        var w = new BrowserLoginWindow { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Cookie == null) return;
        try
        {
            var (acc, updated) = await _app.AddAccountAsync(w.Cookie, _filterGroup);
            _app.Status(updated ? $"Cookie de {acc.Username} mis à jour." : $"{acc.Username} ajouté.");
        }
        catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "Ajout impossible", ex.Message); }
    }

    private async void OnRefreshAll(object sender, RoutedEventArgs e) => await _app.RefreshAllAsync();

    // =====================================================================
    //  Jeux enregistrés + barre de lancement
    // =====================================================================
    private long ParsePlace(string text, out bool bad)
    {
        bad = false;
        if (PlaceBox.SelectedItem is SavedPlace sp && text.Trim() == sp.Label) return sp.PlaceId;
        text = text.Trim();
        if (text.Length == 0) return 0;
        var m = Regex.Match(text, @"/games/(\d+)");
        if (m.Success) return long.Parse(m.Groups[1].Value);
        var all = Regex.Matches(text, @"\d{3,}");
        if (all.Count > 0 && long.TryParse(all[^1].Value, out var id)) return id;
        bad = true;
        return 0;
    }

    // ---- Jeux favoris (barre d'images au-dessus du lancement) ----
    private bool _syncingPlace;

    private void UpdateFavVisibility() => FavList.Visibility = _app.Places.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnFavSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPlace || FavList.SelectedItem is not SavedPlace p) return;
        _syncingPlace = true;
        PlaceBox.SelectedItem = p;
        _syncingPlace = false;
    }

    private void OnPlaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPlace) return;
        _syncingPlace = true;
        FavList.SelectedItem = PlaceBox.SelectedItem as SavedPlace;
        _syncingPlace = false;
    }

    /// <summary>Clic droit : sélectionne d'abord la carte visée, et n'ouvre pas le menu si on a cliqué à côté.</summary>
    private void OnFavRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject) is { } d ? ItemsControl.ContainerFromElement(FavList, d) as ListBoxItem : null;
        if (item == null) { e.Handled = true; return; }
        item.IsSelected = true;
    }

    private void OnFavRemove(object sender, RoutedEventArgs e)
    {
        var p = FavList.SelectedItem as SavedPlace;
        if (p == null) return;
        _app.Places.Remove(p);
        _app.RequestSave();
        _app.Status($"{p.ShortName} retiré des favoris.");
    }

    private async void OnFavRefresh(object sender, RoutedEventArgs e)
    {
        var p = FavList.SelectedItem as SavedPlace;
        if (p == null) return;
        await _app.RefreshPlaceAsync(p);
        _app.Status($"{p.ShortName} actualisé.");
    }

    private async void OnSavePlace(object sender, RoutedEventArgs e)
    {
        var id = ParsePlace(PlaceBox.Text, out var bad);
        if (id == 0 || bad) { _app.Status("Entre d'abord un Place ID valide.", true); return; }
        var p = await _app.AddPlaceAsync(id);
        PlaceBox.SelectedItem = p;
        _app.Status($"Jeu enregistré : {p.Label}");
    }

    private void OnRemovePlace(object sender, RoutedEventArgs e)
    {
        if (PlaceBox.SelectedItem is SavedPlace sp)
        {
            _app.Places.Remove(sp);
            PlaceBox.Text = "";
            _app.RequestSave();
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateModeUi();

    private void UpdateModeUi()
    {
        if (ModeBox?.SelectedValue is not JoinMode m || ValuePanel == null) return;
        ValuePanel.Visibility = m is JoinMode.Place or JoinMode.Home ? Visibility.Collapsed : Visibility.Visible;
        PlaceBox.IsEnabled = StarBtn.IsEnabled = UnstarBtn.IsEnabled = m != JoinMode.Home && m != JoinMode.FollowUser;
        ValueLabel.Text = m switch
        {
            JoinMode.JobId => "Job ID du serveur (nécessite le Place ID à gauche)",
            JoinMode.PrivateServer => "Lien complet ou code du serveur privé",
            JoinMode.FollowUser => "Pseudo ou ID du joueur à rejoindre",
            _ => "",
        };
    }

    private LaunchRequest? BuildRequest()
    {
        var mode = ModeBox.SelectedValue is JoinMode jm ? jm : JoinMode.Place;
        var place = ParsePlace(PlaceBox.Text, out var bad);
        if (bad) { _app.Status("Place ID invalide.", true); return null; }
        var req = new LaunchRequest { Mode = mode, PlaceId = place, Value = ValueBox.Text.Trim() };

        _app.Settings.LastPlaceId = place;
        _app.Settings.LastJoinMode = mode;
        _app.Settings.LastJoinValue = req.Value;
        _app.RequestSave();
        return req;
    }

    private List<Account> SelectedAccounts() => List.SelectedItems.Cast<Account>().ToList();

    private async Task LaunchBatchAsync(IReadOnlyList<Account> accounts)
    {
        if (_cts != null) { _app.Status("Un lancement est déjà en cours.", true); return; }
        if (accounts.Count == 0) { _app.Status("Aucun compte à lancer.", true); return; }
        var req = BuildRequest();
        if (req == null) return;

        _cts = new CancellationTokenSource();
        SetLaunching(true, accounts.Count > 1);
        try
        {
            if (accounts.Count == 1)
            {
                try { await _app.Instances.LaunchAsync(accounts[0], req); }
                catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "Lancement impossible", ex.Message); }
            }
            else
            {
                var todo = accounts.Where(a => !a.IsRunning).ToList();
                int skipped = accounts.Count - todo.Count;
                if (skipped > 0) _app.Status($"{skipped} compte(s) déjà en jeu ignoré(s).");
                await _app.Instances.LaunchManyAsync(todo, req, _cts.Token);
            }
        }
        finally
        {
            _cts.Dispose(); _cts = null;
            SetLaunching(false, false);
        }
    }

    private void SetLaunching(bool on, bool cancellable)
    {
        CancelBtn.Visibility = on && cancellable ? Visibility.Visible : Visibility.Collapsed;
        LaunchSelBtn.IsEnabled = !on;
        LaunchAllBtn.IsEnabled = !on;
    }

    private void OnCancelLaunch(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _app.Status("Lancement annulé après le compte en cours.");
    }

    private async void OnLaunchSelected(object sender, RoutedEventArgs e)
    {
        var sel = SelectedAccounts();
        if (sel.Count == 0) { _app.Status("Sélectionne au moins un compte (clic, Ctrl+clic, Maj+clic).", true); return; }
        await LaunchBatchAsync(sel.OrderBy(a => a.SortOrder).ToList());
    }

    private async void OnLaunchAll(object sender, RoutedEventArgs e)
    {
        var shown = _view.Cast<Account>().ToList();
        if (shown.Count > 3 && !MessageDialog.Show(Window.GetWindow(this), "Tout lancer ?",
                $"Lancer {shown.Count} instance(s) l'une après l'autre ({_app.Settings.LaunchDelaySec}s entre chaque) ?", "Lancer", true)) return;
        await LaunchBatchAsync(shown);
    }

    // =====================================================================
    //  Actions sur les lignes
    // =====================================================================
    private static Account? Acc(object sender) => (sender as FrameworkElement)?.DataContext as Account;

    private async void OnRowLaunch(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is { } a) await LaunchBatchAsync(new[] { a });
    }

    private void OnRowClose(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is { IsRunning: true } a) { _app.Instances.Close(a); _app.Status($"{a.Title} fermé."); }
    }

    private void OnRowEdit(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is { } a) new EditAccountDialog(_app, a) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnRowMore(object sender, RoutedEventArgs e)
    {
        if (Acc(sender) is not { } a) return;
        _menuAcc = a;
        if (!List.SelectedItems.Contains(a)) { List.SelectedItems.Clear(); List.SelectedItems.Add(a); }
        RowMenu.PlacementTarget = (UIElement)sender;
        RowMenu.Placement = PlacementMode.Bottom;
        BuildMoveMenu();
        RowMenu.IsOpen = true;
    }

    private void OnContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (List.SelectedItems.Count == 0) { e.Handled = true; return; }
        BuildMoveMenu();
    }

    private void BuildMoveMenu()
    {
        MoveMenu.Items.Clear();
        MoveMenu.Items.Add(Item("Sans groupe", () => _app.SetGroup(SelectedAccounts(), null)));
        foreach (var g in _app.Groups)
        {
            var grp = g;
            MoveMenu.Items.Add(Item(g.Name, () => _app.SetGroup(SelectedAccounts(), grp.Id)));
        }
        MoveMenu.Items.Add(new Separator());
        MoveMenu.Items.Add(Item("Nouveau groupe…", OnNewGroup));
    }

    /// <summary>Compte visé par les actions « unitaires » du menu : la ligne cliquée, sinon le premier sélectionné.</summary>
    private Account? MenuTarget() => _menuAcc != null && _app.Accounts.Contains(_menuAcc) ? _menuAcc : SelectedAccounts().FirstOrDefault();

    private async void OnMenuLaunch(object sender, RoutedEventArgs e) => await LaunchBatchAsync(SelectedAccounts().OrderBy(a => a.SortOrder).ToList());

    private void OnMenuClose(object sender, RoutedEventArgs e)
    {
        foreach (var a in SelectedAccounts().Where(a => a.IsRunning)) _app.Instances.Close(a);
    }

    private void OnMenuFocus(object sender, RoutedEventArgs e)
    {
        if (MenuTarget() is { IsRunning: true } a) _app.Instances.Focus(a);
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        if (MenuTarget() is { } a) new EditAccountDialog(_app, a) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void OnMenuRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var a in SelectedAccounts()) await _app.RefreshAccountAsync(a);
    }

    private void OnMenuProfile(object sender, RoutedEventArgs e)
    {
        if (MenuTarget() is { } a)
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{a.UserId}/profile") { UseShellExecute = true });
    }

    private void OnMenuCopyName(object sender, RoutedEventArgs e)
    {
        var names = string.Join(Environment.NewLine, SelectedAccounts().Select(a => a.Username));
        if (names.Length > 0) { SecureClipboard.Copy(names); _app.Status("Nom(s) d'utilisateur copié(s)."); }
    }

    private void OnMenuCopyCookie(object sender, RoutedEventArgs e)
    {
        var sel = SelectedAccounts();
        if (sel.Count == 0) return;
        if (!Reauth.Require(Window.GetWindow(this), sel.Count == 1 ? "copier le cookie de ce compte" : $"copier les cookies de {sel.Count} comptes")) return;
        if (SecureClipboard.Copy(string.Join(Environment.NewLine, sel.Select(a => a.Cookie))))
            _app.Status($"{sel.Count} cookie(s) copié(s) : le presse-papiers sera vidé dans 30 s.");
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    // =====================================================================
    //  Sélection
    // =====================================================================
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = List.SelectedItems.Count;
        SelText.Text = n == 0 ? "" : $"{n} sélectionné(s)";
        LaunchSelText.Text = n > 0 ? $"Lancer ({n})" : "Lancer";
    }

    private void OnBulkEdit(object sender, RoutedEventArgs e)
    {
        var sel = SelectedAccounts();
        if (sel.Count == 0) { _app.Status("Sélectionne d'abord au moins un compte.", true); return; }
        var d = new BulkEditDialog(_app, sel) { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() == true) { _view.Refresh(); _app.Status($"{d.Changed} compte(s) modifié(s)."); }
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var sel = SelectedAccounts();
        if (sel.Count == 0) sel = _view.Cast<Account>().ToList();           // sans sélection : les comptes affichés
        var req = BuildRequest();
        if (req == null) return;
        var d = new ProfileDialog(_app, null, sel, req) { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() != true) return;
        _app.Profiles.Add(d.Result);
        _app.RefreshProfiles(); _app.RequestSave();
        _app.Status($"Profil « {d.Result.Name} » créé : retrouve-le dans l'onglet Profils.");
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => List.SelectAll();
    private void OnSelectNone(object sender, RoutedEventArgs e) => List.UnselectAll();

    private void OnBulkClose(object sender, RoutedEventArgs e)
    {
        foreach (var a in SelectedAccounts().Where(a => a.IsRunning)) _app.Instances.Close(a);
    }

    private async void OnBulkRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var a in SelectedAccounts()) await _app.RefreshAccountAsync(a);
        _app.Status("Vérification terminée.");
    }

    private void OnBulkDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var sel = SelectedAccounts();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"« {sel[0].Title} »" : $"{sel.Count} comptes";
        if (!MessageDialog.Show(Window.GetWindow(this), "Supprimer ?",
                $"{what} sera supprimé de l'appli (le compte Roblox, lui, n'est pas touché).", "Supprimer", true, true)) return;
        _app.DeleteAccounts(sel);
        _app.Status($"{sel.Count} compte(s) supprimé(s).");
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Enter && SelectedAccounts().Count > 0)
        {
            await LaunchBatchAsync(SelectedAccounts().OrderBy(a => a.SortOrder).ToList());
            e.Handled = true;
        }
    }

    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (AccountAt(e.OriginalSource as DependencyObject) is { } a) await LaunchBatchAsync(new[] { a });
    }

    // =====================================================================
    //  Glisser-déposer (réordonner / changer de groupe)
    // =====================================================================
    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private Account? AccountAt(DependencyObject? d)
    {
        var item = d == null ? null : ItemsControl.ContainerFromElement(List, d) as ListBoxItem;
        return item?.DataContext as Account;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragAcc = IsInsideButton(e.OriginalSource as DependencyObject) ? null : AccountAt(e.OriginalSource as DependencyObject);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragAcc == null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _dragAcc;
        _dragAcc = null;
        if (!List.SelectedItems.Contains(dragged)) { List.SelectedItems.Clear(); List.SelectedItems.Add(dragged); }
        var data = new DataObject("bb.accounts", SelectedAccounts().ToArray());
        DragDrop.DoDragDrop(List, data, DragDropEffects.Move);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("bb.accounts") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("bb.accounts") is not Account[] dropped) return;
        var target = AccountAt(e.OriginalSource as DependencyObject);
        if (target == null) return;
        foreach (var a in dropped.OrderBy(a => a.SortOrder).Where(a => a != target))
            _app.ReorderBefore(a, target);
        _view.Refresh();
        e.Handled = true;
    }
}
