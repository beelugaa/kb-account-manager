using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using BBManager.Core;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class ProfileDialog : Window
{
    private sealed class Pick : ObservableObject
    {
        public Account Account { get; init; } = null!;
        private bool _sel; public bool Selected { get => _sel; set => Set(ref _sel, value); }
    }
    private sealed record ModeItem(JoinMode Value, string Label) { public override string ToString() => Label; }

    private static readonly string[] Days = { "Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim" };

    private readonly AppState _app;
    private readonly LaunchProfile _profile;
    private readonly bool _isNew;
    private readonly List<Pick> _picks;
    private readonly ICollectionView _view;
    private readonly List<ToggleButton> _dayButtons = new();

    public LaunchProfile Result => _profile;

    /// <param name="existing">Profil à modifier (null = nouveau).</param>
    /// <param name="preselect">Comptes cochés d'office pour un nouveau profil.</param>
    /// <param name="seed">Jeu / mode proposés d'office pour un nouveau profil.</param>
    public ProfileDialog(AppState app, LaunchProfile? existing = null, IEnumerable<Account>? preselect = null, LaunchRequest? seed = null)
    {
        InitializeComponent();
        _app = app;
        _isNew = existing == null;
        _profile = existing ?? new LaunchProfile { Name = "" };
        TitleText.Text = _isNew ? "Nouveau profil" : "Modifier le profil";

        var chosen = existing?.AccountIds.ToHashSet() ?? preselect?.Select(a => a.Id).ToHashSet() ?? new HashSet<Guid>();
        _picks = app.Accounts.OrderBy(a => a.SortOrder).Select(a => new Pick { Account = a, Selected = chosen.Contains(a.Id) }).ToList();
        foreach (var p in _picks) p.PropertyChanged += (_, _) => UpdateCount();
        _view = CollectionViewSource.GetDefaultView(_picks);
        AccountList.ItemsSource = _view;

        ModeBox.ItemsSource = new[]
        {
            new ModeItem(JoinMode.Place, "Rejoindre un jeu"),
            new ModeItem(JoinMode.Home, "Accueil de Roblox"),
            new ModeItem(JoinMode.JobId, "Serveur précis (Job ID)"),
            new ModeItem(JoinMode.PrivateServer, "Serveur privé"),
            new ModeItem(JoinMode.FollowUser, "Rejoindre un joueur"),
        };
        PlaceBox.ItemsSource = app.Places;

        var mode = existing?.Mode ?? seed?.Mode ?? JoinMode.Place;
        ModeBox.SelectedItem = ((ModeItem[])ModeBox.ItemsSource).First(m => m.Value == mode);
        var place = existing?.PlaceId ?? seed?.PlaceId ?? 0;
        if (place != 0)
        {
            var sp = app.Places.FirstOrDefault(x => x.PlaceId == place);
            if (sp != null) PlaceBox.SelectedItem = sp; else PlaceBox.Text = place.ToString();
        }
        ValueBox.Text = existing?.Value ?? seed?.Value ?? "";

        NameBox.Text = _profile.Name;
        RamBox.Text = _profile.RamLimitMb.ToString();
        TileBox.IsChecked = _profile.TileAfter;
        SchedBox.IsChecked = _profile.ScheduleEnabled;
        TimeBox.Text = _profile.ScheduleTime;

        for (int i = 0; i < 7; i++)
        {
            var tb = new ToggleButton
            {
                Content = Days[i], Width = 52, Margin = new Thickness(0, 0, 6, 6), IsChecked = (_profile.ScheduleDays & (1 << i)) != 0,
                Style = (Style)FindResource("DayToggle"),
            };
            _dayButtons.Add(tb);
            DaysPanel.Children.Add(tb);
        }

        UpdateCount();
        OnModeChanged(this, null!);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void UpdateCount() => CountText.Text = $"{_picks.Count(p => p.Selected)} compte(s) coché(s) sur {_picks.Count}";

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _view.Filter = q.Length == 0 ? null : o => ((Pick)o).Account.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                || ((Pick)o).Account.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                || ((Pick)o).Account.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnCheckAll(object sender, RoutedEventArgs e) { foreach (Pick p in _view) p.Selected = true; }
    private void OnCheckNone(object sender, RoutedEventArgs e) { foreach (Pick p in _view) p.Selected = false; }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedItem is not ModeItem m) return;
        PlaceBox.IsEnabled = m.Value is JoinMode.Place or JoinMode.JobId or JoinMode.PrivateServer;
        ValueBox.Visibility = m.Value is JoinMode.JobId or JoinMode.PrivateServer or JoinMode.FollowUser ? Visibility.Visible : Visibility.Collapsed;
        GameHint.Text = m.Value switch
        {
            JoinMode.Place => "Laisse le jeu vide pour que chaque compte rejoigne son « Place ID par défaut ».",
            JoinMode.Home => "Ouvre simplement le client Roblox sur la page d'accueil.",
            JoinMode.JobId => "Place ID ci-dessus + Job ID du serveur ci-dessous.",
            JoinMode.PrivateServer => "Lien complet ou code du serveur privé ci-dessous (Place ID facultatif si le lien le contient).",
            _ => "Pseudo ou ID du joueur à rejoindre ci-dessous.",
        };
        ValueBox.ToolTip = m.Value switch { JoinMode.JobId => "Job ID du serveur", JoinMode.PrivateServer => "Lien ou code du serveur privé", _ => "Pseudo ou ID du joueur" };
    }

    private long ParsePlace(out bool bad)
    {
        bad = false;
        var text = (PlaceBox.SelectedItem is SavedPlace sp ? sp.PlaceId.ToString() : PlaceBox.Text).Trim();
        if (text.Length == 0) return 0;
        var m = Regex.Match(text, @"/games/(\d+)");
        if (m.Success) return long.Parse(m.Groups[1].Value);
        var all = Regex.Matches(text, @"\d{3,}");
        if (all.Count > 0 && long.TryParse(all[^1].Value, out var id)) return id;
        bad = true;
        return 0;
    }

    private void Fail(string m) { ErrText.Text = m; ErrText.Visibility = Visibility.Visible; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Fail("Donne un nom au profil."); NameBox.Focus(); return; }
        var ids = _picks.Where(p => p.Selected).Select(p => p.Account.Id).ToList();
        if (ids.Count == 0) { Fail("Coche au moins un compte."); return; }

        var mode = ((ModeItem)ModeBox.SelectedItem).Value;
        long place = ParsePlace(out var bad);
        if (bad) { Fail("Place ID invalide."); return; }
        var value = ValueBox.Text.Trim();
        if (mode is JoinMode.JobId or JoinMode.PrivateServer or JoinMode.FollowUser && value.Length == 0)
        { Fail("Renseigne la valeur demandée (Job ID, lien du serveur privé ou joueur)."); return; }

        int ram = 0;
        if (RamBox.Text.Trim().Length > 0 && (!int.TryParse(RamBox.Text.Trim(), out ram) || ram < 0 || ram > 131072)) { Fail("Limite RAM invalide."); return; }

        var time = TimeBox.Text.Trim();
        if (SchedBox.IsChecked == true && !Regex.IsMatch(time, @"^([01]?\d|2[0-3]):[0-5]\d$")) { Fail("Heure invalide : utilise le format HH:mm (par exemple 18:30)."); return; }
        if (Regex.IsMatch(time, @"^\d:\d\d$")) time = "0" + time;

        _profile.Name = name;
        _profile.AccountIds = ids;
        _profile.Mode = mode;
        _profile.PlaceId = place;
        _profile.Value = value;
        _profile.RamLimitMb = ram;
        _profile.TileAfter = TileBox.IsChecked == true;
        _profile.ScheduleEnabled = SchedBox.IsChecked == true;
        _profile.ScheduleTime = Regex.IsMatch(time, @"^([01]\d|2[0-3]):[0-5]\d$") ? time : "18:00";
        _profile.ScheduleDays = Enumerable.Range(0, 7).Where(i => _dayButtons[i].IsChecked == true).Sum(i => 1 << i);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
