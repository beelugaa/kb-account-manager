using System.Windows;

namespace BBManager.Views;

public partial class MessageDialog : Window
{
    private MessageDialog() { InitializeComponent(); }

    /// <summary>Boîte de dialogue au style de l'appli. Retourne true si l'utilisateur confirme.</summary>
    public static bool Show(Window? owner, string title, string message, string ok = "OK", bool cancel = false, bool danger = false)
    {
        var d = new MessageDialog { Owner = owner };
        if (owner == null) d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.TitleText.Text = title;
        d.Body.Text = message;
        d.OkBtn.Content = ok;
        d.CancelBtn.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger) d.OkBtn.Style = (Style)Application.Current.FindResource("DangerButton");
        return d.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
