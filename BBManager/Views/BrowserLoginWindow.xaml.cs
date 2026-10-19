using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace BBManager.Views;

/// <summary>Fenêtre de connexion Roblox dans une session WebView2 vierge ; récupère le cookie .ROBLOSECURITY.</summary>
public partial class BrowserLoginWindow : Window
{
    private readonly string _profileDir = Path.Combine(Path.GetTempPath(), "bbmanager-login-" + Guid.NewGuid().ToString("N"));
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _checking;

    public string? Cookie { get; private set; }

    public BrowserLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _profileDir);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, a) => a.Handled = true;                                  // pas de fenêtres surgissantes
            core.DownloadStarting += (_, a) => a.Cancel = true;                                    // aucun téléchargement
            core.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;      // caméra, micro, position… refusés
            core.NavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !a.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
                    a.Cancel = true;                                                                // HTTPS uniquement
            };
            core.Navigate("https://www.roblox.com/login");
            _poll.Tick += async (_, _) => await PollAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Hint.Text = "WebView2 indisponible : " + ex.Message + " — utilise plutôt l'ajout par cookie.";
        }
    }

    private async Task PollAsync()
    {
        if (_checking || Web.CoreWebView2 == null) return;
        _checking = true;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var c = cookies.FirstOrDefault(x => x.Name == ".ROBLOSECURITY");
            if (c != null && c.Value.Contains("_|WARNING"))
            {
                _poll.Stop();
                Cookie = c.Value;
                DialogResult = true;
            }
        }
        catch { /* navigation en cours */ }
        finally { _checking = false; }
    }

    private void Cleanup()
    {
        _poll.Stop();
        try { Web.CoreWebView2?.Profile?.ClearBrowsingDataAsync(); } catch { }                     // efface cookies et cache de la session
        try { Web.Dispose(); } catch { }
        Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, true); return; }
                catch { await Task.Delay(1000); }
            }
        });
    }
}
