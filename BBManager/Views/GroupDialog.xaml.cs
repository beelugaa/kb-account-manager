using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BBManager.Views;

public partial class GroupDialog : Window
{
    public static readonly string[] Palette =
        { "#18B8FF", "#7C5CFF", "#3D8BFF", "#2EC4B6", "#3DDC97", "#FFB84D", "#FF7A45", "#FF5C7A", "#D65CFF", "#8B91A1" };

    private string _color;
    public string GroupName => NameBox.Text.Trim();
    public string GroupColor => _color;

    public GroupDialog(string name = "", string color = "#7C5CFF", bool edit = false)
    {
        InitializeComponent();
        _color = color;
        NameBox.Text = name;
        if (edit) TitleText.Text = "Modifier le groupe";
        BuildSwatches();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var hex in Palette)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var el = new Ellipse
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), Fill = new SolidColorBrush(c),
                Stroke = hex == _color ? Brushes.White : Brushes.Transparent, StrokeThickness = 2.5,
                Cursor = Cursors.Hand,
            };
            el.MouseLeftButtonUp += (_, _) => { _color = hex; BuildSwatches(); };
            Swatches.Children.Add(el);
        }
    }

    private void OnKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnOk(sender, e); }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
