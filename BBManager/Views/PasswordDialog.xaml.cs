using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BBManager.Services;

namespace BBManager.Views;

public partial class PasswordDialog : Window
{
    public enum PasswordMode { Unlock, Set, Confirm }

    private static readonly string[] Common =
    {
        "password", "motdepasse", "12345678", "123456789", "1234567890", "azertyui", "azerty123", "qwertyui", "qwerty123",
        "iloveyou", "11111111", "00000000", "roblox123", "robloxroblox", "abcd1234", "admin123",
    };

    private readonly PasswordMode _mode;
    private readonly Func<string, bool>? _verify;
    private readonly DispatcherTimer _wait = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public string Password { get; private set; } = "";
    /// <summary>L'utilisateur a choisi « J'ai oublié mon mot de passe » puis « Réinitialiser l'appli » (et confirmé).</summary>
    public bool ResetRequested { get; private set; }

    /// <param name="verify">Unlock / Confirm : retourne true si le mot de passe est bon.</param>
    public PasswordDialog(PasswordMode mode, Func<string, bool>? verify = null, string? title = null, string? message = null)
    {
        InitializeComponent();
        _mode = mode;
        _verify = verify;

        switch (mode)
        {
            case PasswordMode.Set:
                TitleText.Text = title ?? "Mot de passe";
                SubText.Text = message ?? "Il chiffre tes cookies et sera demandé à chaque lancement. Il ne peut pas être récupéré : note-le quelque part de sûr.";
                Pwd2.Visibility = Visibility.Visible; Strength.Visibility = Visibility.Visible;
                Pwd1.ToolTip = "Nouveau mot de passe (8 caractères minimum)"; Pwd2.ToolTip = "Confirmer le mot de passe";
                OkBtn.Content = "Enregistrer";
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                break;
            case PasswordMode.Confirm:
                TitleText.Text = title ?? "Confirme ton mot de passe";
                SubText.Text = message ?? "Cette action est sensible.";
                OkBtn.Content = "Confirmer";
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                break;
            default:
                if (title != null) TitleText.Text = title;
                ForgotBtn.Visibility = Visibility.Visible;
                break;
        }

        _wait.Tick += (_, _) => UpdateWait();
        Loaded += (_, _) => { Pwd1.Focus(); UpdateWait(); };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }

    private void OnPwdChanged(object sender, RoutedEventArgs e)
    {
        if (_mode != PasswordMode.Set) return;
        var p = Pwd1.Password;
        if (p.Length == 0) { Strength.Text = ""; return; }
        int score = 0;
        if (p.Length >= 8) score++;
        if (p.Length >= 12) score++;
        if (p.Any(char.IsUpper) && p.Any(char.IsLower)) score++;
        if (p.Any(char.IsDigit)) score++;
        if (p.Any(c => !char.IsLetterOrDigit(c))) score++;
        Strength.Text = score switch { <= 2 => "Force : faible", 3 => "Force : moyenne", 4 => "Force : bonne", _ => "Force : excellente" };
        Strength.Foreground = (System.Windows.Media.Brush)FindResource(score <= 2 ? "BadBrush" : score == 3 ? "WarnBrush" : "GoodBrush");
    }

    /// <summary>Après plusieurs mots de passe faux, les essais sont ralentis.</summary>
    private void UpdateWait()
    {
        var left = AttemptGuard.Remaining;
        if (_mode == PasswordMode.Set) return;
        if (left > TimeSpan.Zero)
        {
            OkBtn.IsEnabled = false;
            Fail($"Trop d'essais. Réessaie dans {Math.Ceiling(left.TotalSeconds)} s.");
            _wait.Start();
        }
        else
        {
            if (!OkBtn.IsEnabled) { OkBtn.IsEnabled = true; Err.Visibility = Visibility.Collapsed; }
            _wait.Stop();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!OkBtn.IsEnabled) return;

        if (_mode == PasswordMode.Set)
        {
            var p = Pwd1.Password;
            if (p.Length < 8) { Fail("8 caractères minimum."); return; }
            if (Common.Contains(p.ToLowerInvariant())) { Fail("Ce mot de passe est trop courant : choisis-en un autre."); return; }
            if (p != Pwd2.Password) { Fail("Les deux mots de passe sont différents."); return; }
            Password = p;
            DialogResult = true;
            return;
        }

        if (Pwd1.Password.Length == 0) { Fail("Entre ton mot de passe."); return; }
        bool ok;
        try { ok = _verify?.Invoke(Pwd1.Password) ?? false; }
        catch (Exception ex) { Fail(ex.Message); return; }

        if (ok)
        {
            AttemptGuard.Success();
            Password = Pwd1.Password;
            DialogResult = true;
        }
        else
        {
            AttemptGuard.Failure();
            Pwd1.Clear(); Pwd1.Focus();
            Fail("Mot de passe incorrect.");
            UpdateWait();
        }
    }

    private void OnForgot(object sender, RoutedEventArgs e)
    {
        if (ForgotPasswordDialog.Ask(this))
        {
            ResetRequested = true;
            DialogResult = true;
        }
    }

    private void Fail(string m) { Err.Text = m; Err.Visibility = Visibility.Visible; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
