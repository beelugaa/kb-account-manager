using System.Windows;

namespace BBManager.Views;

/// <summary>« J'ai oublié mon mot de passe » : choix entre « Non » et « Réinitialiser l'appli » (avec une dernière confirmation).</summary>
public partial class ForgotPasswordDialog : Window
{
    private ForgotPasswordDialog() { InitializeComponent(); Loaded += (_, _) => NoBtn.Focus(); }

    /// <summary>Retourne true seulement si l'utilisateur a choisi de tout réinitialiser ET l'a confirmé.</summary>
    public static bool Ask(Window? owner)
    {
        var d = new ForgotPasswordDialog { Owner = owner };
        if (owner == null) d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (d.ShowDialog() != true) return false;

        return MessageDialog.Show(owner, "Dernière confirmation",
            "Tous les comptes, groupes et réglages enregistrés vont être supprimés définitivement. Cette action est irréversible.",
            "Oui, tout supprimer", cancel: true, danger: true);
    }

    private void OnNo(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnReset(object sender, RoutedEventArgs e) => DialogResult = true;
}
