using System.Windows;
using System.Windows.Controls;
using BBManager.Services;

namespace BBManager.Views;

/// <summary>Liste des changements de la version en cours (texte embarqué dans l'appli, une puce par ligne).</summary>
public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateService.CurrentText}";
        TitleText.Text = "Nouveautés";
        foreach (var raw in UpdateService.WhatsNew.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            bool bullet = line.StartsWith("- ") || line.StartsWith("• ");
            if (bullet) line = line[2..].Trim();

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (bullet) row.Children.Add(new TextBlock { Text = "•", Style = (Style)FindResource("Dim") });
            var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Style = (Style)FindResource("Dim") };
            Grid.SetColumn(text, bullet ? 1 : 0);
            if (!bullet) Grid.SetColumnSpan(text, 2);
            row.Children.Add(text);
            Items.Children.Add(row);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}
