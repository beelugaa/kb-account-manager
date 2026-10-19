using System.Windows;
using System.Windows.Media;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

public partial class EditAccountDialog : Window
{
    private readonly AppState _app;
    private readonly Account _acc;
    private string _newCookie;

    public EditAccountDialog(AppState app, Account acc)
    {
        InitializeComponent();
        _app = app; _acc = acc; _newCookie = acc.Cookie;

        NameText.Text = acc.Username;
        IdText.Text = $"{acc.DisplayName} · ID {acc.UserId}";
        if (!string.IsNullOrEmpty(acc.AvatarUrl))
            try { Avatar.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(acc.AvatarUrl)); } catch { }

        AliasBox.Text = acc.Alias;
        NotesBox.Text = acc.Notes;
        PlaceBox.Text = acc.PlaceId == 0 ? "" : acc.PlaceId.ToString();
        RamBox.Text = acc.RamLimitMb.ToString();
        KeepAliveBox.IsChecked = acc.KeepAlive;
        AfkBox.IsChecked = acc.AntiAfk;

        var groups = new List<AccountGroup> { new() { Id = Guid.Empty, Name = "Sans groupe" } };
        groups.AddRange(app.Groups);
        GroupBox.ItemsSource = groups;
        GroupBox.SelectedItem = groups.FirstOrDefault(g => g.Id == acc.GroupId) ?? groups[0];
    }

    private void OnCopyCookie(object sender, RoutedEventArgs e)
    {
        if (!Reauth.Require(this, "copier le cookie de ce compte")) return;
        SecureClipboard.Copy(_newCookie);
        Info.Text = "Cookie copié : le presse-papiers sera vidé dans 30 s."; Info.Foreground = (Brush)FindResource("GoodBrush");
    }

    private async void OnReplaceCookie(object sender, RoutedEventArgs e)
    {
        var raw = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        var cookie = RobloxApi.NormalizeCookie(raw);
        if (!cookie.Contains("_|WARNING"))
        {
            Info.Text = "Copie d'abord le nouveau cookie (il est lu depuis le presse-papiers), puis reclique.";
            Info.Foreground = (Brush)FindResource("WarnBrush");
            return;
        }
        Info.Text = "Vérification du cookie…"; Info.Foreground = (Brush)FindResource("DimBrush");
        try
        {
            var u = await RobloxApi.GetAuthenticatedUserAsync(cookie);
            if (u == null) throw new InvalidOperationException("Cookie refusé par Roblox.");
            if (u.Id != _acc.UserId) throw new InvalidOperationException($"Ce cookie appartient à un autre compte ({u.Name}).");
            _newCookie = cookie;
            Info.Text = "Nouveau cookie validé — clique sur Enregistrer."; Info.Foreground = (Brush)FindResource("GoodBrush");
        }
        catch (Exception ex) { Info.Text = ex.Message; Info.Foreground = (Brush)FindResource("BadBrush"); }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        long place = 0;
        if (!string.IsNullOrWhiteSpace(PlaceBox.Text) && !long.TryParse(PlaceBox.Text.Trim(), out place))
        { Info.Text = "Place ID invalide."; Info.Foreground = (Brush)FindResource("BadBrush"); return; }
        int ram = 0;
        if (!string.IsNullOrWhiteSpace(RamBox.Text) && (!int.TryParse(RamBox.Text.Trim(), out ram) || ram < 0))
        { Info.Text = "Limite RAM invalide."; Info.Foreground = (Brush)FindResource("BadBrush"); return; }

        _acc.Alias = AliasBox.Text.Trim();
        _acc.Notes = NotesBox.Text.Trim();
        _acc.PlaceId = place;
        _acc.RamLimitMb = ram;
        _acc.KeepAlive = KeepAliveBox.IsChecked == true;
        _acc.AntiAfk = AfkBox.IsChecked == true;
        _acc.Cookie = _newCookie;
        _acc.CookieValid = true;
        var gid = (GroupBox.SelectedItem as AccountGroup)?.Id ?? Guid.Empty;
        _acc.GroupId = gid == Guid.Empty ? null : gid;

        _acc.RefreshIdentity();
        _app.RebuildGroupInfo();
        if (_acc.IsRunning) _app.Instances.ApplyProcessSettings(_acc);
        _app.RequestSave();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
