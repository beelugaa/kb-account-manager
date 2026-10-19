using System.Windows;

namespace BBManager.Views;

/// <summary>Question posée au tout premier lancement : protéger l'appli par un mot de passe ?</summary>
public partial class FirstRunDialog : Window
{
    private FirstRunDialog() { InitializeComponent(); }

    public string? Chosen { get; private set; }

    /// <summary>Retourne le mot de passe choisi, ou null si l'utilisateur n'en veut pas.</summary>
    public static string? Ask()
    {
        var d = new FirstRunDialog();
        d.ShowDialog();
        return d.Chosen;
    }

    private void OnSet(object sender, RoutedEventArgs e)
    {
        var p = new PasswordDialog(PasswordDialog.PasswordMode.Set) { Owner = this };
        if (p.ShowDialog() != true) return;          // annulé : on reste sur la question
        Chosen = p.Password;
        Close();
    }

    private void OnNo(object sender, RoutedEventArgs e) { Chosen = null; Close(); }
}
