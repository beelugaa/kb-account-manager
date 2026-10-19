using System.Windows;
using BBManager.Services;

namespace BBManager.Views;

/// <summary>Confirmation avant une action sensible (copier / exporter des cookies, changer le mot de passe, réinitialiser).</summary>
public static class Reauth
{
    private static DateTime _okUntil = DateTime.MinValue;

    /// <param name="what">Action à l'infinitif : « copier un cookie », « exporter tous les comptes »…</param>
    public static bool Require(Window? owner, string what)
    {
        if (DataStore.HasPassword)
        {
            if (DateTime.Now < _okUntil) return true;           // confirmé il y a moins d'une minute
            var d = new PasswordDialog(PasswordDialog.PasswordMode.Confirm, DataStore.VerifyPassword,
                "Confirme ton mot de passe", $"Pour {what}.") { Owner = owner };
            if (d.ShowDialog() != true) return false;
            _okUntil = DateTime.Now.AddSeconds(60);
            return true;
        }
        return MessageDialog.Show(owner, "Action sensible", $"Tu vas {what}. Continuer ?", "Continuer", cancel: true);
    }

    public static void Forget() => _okUntil = DateTime.MinValue;
}
