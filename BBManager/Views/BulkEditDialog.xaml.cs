using System.Windows;
using BBManager.Models;
using BBManager.Services;

namespace BBManager.Views;

/// <summary>Modifie en une fois plusieurs comptes : groupe, jeu, RAM, relance auto, anti-AFK, alias, notes.</summary>
public partial class BulkEditDialog : Window
{
    private sealed record Opt<T>(T Value, string Label) { public override string ToString() => Label; }

    private readonly AppState _app;
    private readonly List<Account> _accounts;

    public int Changed { get; private set; }

    public BulkEditDialog(AppState app, List<Account> accounts)
    {
        InitializeComponent();
        _app = app;
        _accounts = accounts.OrderBy(a => a.SortOrder).ToList();
        TitleText.Text = _accounts.Count == 1 ? $"Modifier {_accounts[0].Title}" : $"Modifier {_accounts.Count} comptes";

        var groups = new List<AccountGroup> { new() { Id = Guid.Empty, Name = "Sans groupe" } };
        groups.AddRange(app.Groups);
        GroupBox.ItemsSource = groups; GroupBox.SelectedIndex = 0;

        KeepBox.ItemsSource = new[] { new Opt<bool>(true, "Activée"), new Opt<bool>(false, "Désactivée") }; KeepBox.SelectedIndex = 0;
        AfkBox.ItemsSource = new[] { new Opt<bool>(true, "Activé"), new Opt<bool>(false, "Désactivé") }; AfkBox.SelectedIndex = 0;
        MethodBox.ItemsSource = new[]
        {
            new Opt<AfkMethod?>(null, "Méthode par défaut"),
            new Opt<AfkMethod?>(AfkMethod.Jump, "Saut (touche Espace)"),
            new Opt<AfkMethod?>(AfkMethod.Discreet, "Discret (touche F15)"),
        };
        MethodBox.SelectedIndex = 0;
        NotesMode.ItemsSource = new[] { new Opt<bool>(false, "Remplacer les notes"), new Opt<bool>(true, "Ajouter à la fin des notes") };
        NotesMode.SelectedIndex = 0;

        PlaceBox.Text = ""; RamBox.Text = "0"; IntervalBox.Text = "0"; AliasBox.Text = "";
    }

    private void Fail(string m) { ErrText.Text = m; ErrText.Visibility = Visibility.Visible; }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // ---- validation d'abord : on n'applique rien si un champ est faux
        long place = 0; int ram = 0, interval = 0;
        if (PlaceCheck.IsChecked == true && PlaceBox.Text.Trim().Length > 0 && (!long.TryParse(PlaceBox.Text.Trim(), out place) || place < 0))
        { Fail("Place ID invalide (chiffres uniquement)."); return; }
        if (RamCheck.IsChecked == true && (!int.TryParse(RamBox.Text.Trim(), out ram) || ram < 0 || ram > 131072))
        { Fail("Limite RAM invalide."); return; }
        if (IntervalCheck.IsChecked == true && (!int.TryParse(IntervalBox.Text.Trim(), out interval) || interval < 0 || (interval > 0 && interval < 15)))
        { Fail("Intervalle AFK invalide : 0 (par défaut) ou 15 s minimum."); return; }
        if (IntervalCheck.IsChecked == true && interval > 1200 &&
            !MessageDialog.Show(this, "Intervalle trop long", "Au-delà de 20 minutes (1200 s), Roblox peut expulser le compte avant l'envoi. Continuer ?", "Continuer", true)) return;

        bool any = new[] { GroupCheck, PlaceCheck, RamCheck, KeepCheck, AfkCheck, MethodCheck, IntervalCheck, AliasCheck, NotesCheck }.Any(c => c.IsChecked == true);
        if (!any) { Fail("Active au moins un réglage à modifier."); return; }

        // ---- application
        var group = (GroupBox.SelectedItem as AccountGroup)?.Id;
        int n = 0;
        foreach (var a in _accounts)
        {
            n++;
            if (GroupCheck.IsChecked == true) a.GroupId = group == Guid.Empty ? null : group;
            if (PlaceCheck.IsChecked == true) a.PlaceId = place;
            if (RamCheck.IsChecked == true) a.RamLimitMb = ram;
            if (KeepCheck.IsChecked == true) a.KeepAlive = ((Opt<bool>)KeepBox.SelectedItem).Value;
            if (AfkCheck.IsChecked == true) { a.AntiAfk = ((Opt<bool>)AfkBox.SelectedItem).Value; a.NextAfkAt = null; }
            if (MethodCheck.IsChecked == true) { a.AfkMethodOverride = ((Opt<AfkMethod?>)MethodBox.SelectedItem).Value; a.NextAfkAt = null; }
            if (IntervalCheck.IsChecked == true) { a.AfkIntervalSec = interval; a.NextAfkAt = null; }
            if (AliasCheck.IsChecked == true)
                a.Alias = AliasBox.Text.Replace("{n}", n.ToString()).Replace("{nom}", a.Username).Trim();
            if (NotesCheck.IsChecked == true)
            {
                var add = NotesBox.Text.Trim();
                a.Notes = ((Opt<bool>)NotesMode.SelectedItem).Value
                    ? (a.Notes.Length == 0 ? add : a.Notes + Environment.NewLine + add)
                    : add;
            }
            a.RefreshIdentity();
            a.RefreshAfkConfig();
            if (a.IsRunning) _app.Instances.ApplyProcessSettings(a);
        }

        Changed = _accounts.Count;
        _app.RebuildGroupInfo();
        _app.RefreshProfiles();
        _app.RequestSave();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
