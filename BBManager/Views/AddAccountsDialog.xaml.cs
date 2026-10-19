using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBManager.Models;
using BBManager.Services;
using Microsoft.Win32;

namespace BBManager.Views;

public partial class AddAccountsDialog : Window
{
    private readonly AppState _app;
    private static readonly Regex CookieRx = new(@"_\|WARNING:-DO-NOT-SHARE-THIS[^\s;""',]+", RegexOptions.Compiled);

    public AddAccountsDialog(AppState app, Guid? preselectGroup)
    {
        InitializeComponent();
        _app = app;

        var items = new List<AccountGroup> { new() { Id = Guid.Empty, Name = "Sans groupe" } };
        items.AddRange(app.Groups);
        GroupBox.ItemsSource = items;
        GroupBox.SelectedItem = items.FirstOrDefault(g => g.Id == preselectGroup) ?? items[0];
        Loaded += (_, _) => Input.Focus();
    }

    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Export KB Manager chiffré|*.kbx|Fichiers texte|*.txt;*.csv;*.log|Tous les fichiers|*.*",
            Title = "Importer des comptes",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 5_000_000) { MessageDialog.Show(this, "Fichier trop gros", "Le fichier dépasse 5 Mo : ce n'est pas une liste de cookies."); return; }

            if (info.Extension.Equals(".kbx", StringComparison.OrdinalIgnoreCase))
            {
                var blob = File.ReadAllBytes(dlg.FileName);
                byte[]? plain = null;
                var pd = new PasswordDialog(PasswordDialog.PasswordMode.Confirm, p =>
                {
                    try { plain = DataStore.DecryptBlob(blob, p); return true; }
                    catch (System.Security.Cryptography.CryptographicException) { return false; }
                }, "Mot de passe de l'export", "Entre le mot de passe choisi lors de l'export.") { Owner = this };
                if (pd.ShowDialog() != true || plain == null) return;
                Input.Text = System.Text.Encoding.UTF8.GetString(plain);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            }
            else Input.Text = File.ReadAllText(dlg.FileName);
        }
        catch (Exception ex) { MessageDialog.Show(this, "Lecture impossible", ex.Message); }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var cookies = CookieRx.Matches(Input.Text).Select(m => m.Value).Distinct().Take(500).ToList();
        if (cookies.Count == 0)
        {
            Progress.Text = "Aucun cookie .ROBLOSECURITY détecté dans le texte.";
            Progress.Foreground = (Brush)FindResource("BadBrush");
            return;
        }

        var groupId = (GroupBox.SelectedItem as AccountGroup)?.Id is { } gid && gid != Guid.Empty ? gid : (Guid?)null;

        AddBtn.IsEnabled = false; Input.Visibility = Visibility.Collapsed; Results.Visibility = Visibility.Visible;
        Results.Items.Clear();
        int ok = 0;

        for (int i = 0; i < cookies.Count; i++)
        {
            Progress.Text = $"Vérification {i + 1}/{cookies.Count}…";
            Progress.Foreground = (Brush)FindResource("DimBrush");
            try
            {
                var (acc, updated) = await _app.AddAccountAsync(cookies[i], groupId);
                ok++;
                Results.Items.Add(Row("✓", $"{acc.Username}  {(updated ? "(cookie mis à jour)" : "ajouté")}", "GoodBrush"));
            }
            catch (Exception ex)
            {
                Results.Items.Add(Row("✗", $"Cookie {i + 1} : {ex.Message}", "BadBrush"));
            }
        }

        Progress.Text = $"{ok}/{cookies.Count} compte(s) ajouté(s).";
        Progress.Foreground = (Brush)FindResource(ok == cookies.Count ? "GoodBrush" : "WarnBrush");
        AddBtn.Visibility = Visibility.Collapsed;
        CloseBtn.Style = (Style)FindResource("PrimaryButton");
    }

    private UIElement Row(string icon, string text, string brushKey)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = icon, Foreground = (Brush)FindResource(brushKey), FontWeight = FontWeights.Bold, Width = 22 });
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
