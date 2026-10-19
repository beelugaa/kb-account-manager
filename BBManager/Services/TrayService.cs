using System.Windows;
using BBManager.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BBManager.Services;

/// <summary>Icône de la barre système : ouvrir / masquer la fenêtre, raccourcis, quitter.</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppState _app;
    private readonly Window _win;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _afkItem;
    private readonly Forms.ToolStripMenuItem _lockItem;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private bool _hintShown;

    public TrayService(AppState app, Window win)
    {
        _app = app;
        _win = win;

        Drawing.Icon icon;
        using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico")).Stream)
            icon = new Drawing.Icon(s);

        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(0x1A, 0x1D, 0x24),
            ForeColor = Drawing.Color.FromArgb(0xE8, 0xEA, 0xF0),
            Font = new Drawing.Font("Segoe UI", 9.5f),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()),
        };
        menu.Items.Add(Item("Ouvrir KB Manager", ShowWindow, bold: true));
        _updateItem = Item("Mise à jour disponible…", () => { ShowWindow(); (Application.Current.MainWindow as Views.MainWindow)?.ShowUpdateDialog(); }, bold: true);
        _updateItem.Visible = false;
        menu.Items.Add(_updateItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _afkItem = Item("Anti-AFK activé", () => { _app.Settings.AfkEnabled = !_app.Settings.AfkEnabled; _app.RequestSave(); });
        menu.Items.Add(_afkItem);
        _lockItem = Item("Verrouiller (Ctrl+L)", () => (Application.Current.MainWindow as Views.MainWindow)?.Lock());
        menu.Items.Add(_lockItem);
        menu.Items.Add(Item("Ranger les fenêtres", () => _app.Instances.TileWindows()));
        menu.Items.Add(Item("Libérer la RAM", () => _app.Status($"RAM libérée sur {_app.Instances.TrimAllNow()} instance(s).")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quitter (les Roblox restent ouverts)", () => App.Quit()));
        menu.Opening += (_, _) => { _updateItem.Visible = _app.Updates.HasUpdate; if (_app.Updates.Available != null) _updateItem.Text = $"Mise à jour {_app.Updates.Available.VersionText} disponible…"; _afkItem.Checked = _app.Settings.AfkEnabled; _lockItem.Visible = DataStore.HasPassword; };

        _icon = new Forms.NotifyIcon { Icon = icon, Visible = true, Text = "KB Account Manager", ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (_win.IsVisible && _win.WindowState != WindowState.Minimized && _win.IsActive) _win.Hide();
            else ShowWindow();
        };

        _app.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.StatsText)) UpdateText(); };
        UpdateText();
    }

    private static Forms.ToolStripMenuItem Item(string text, Action act, bool bold = false)
    {
        var mi = new Forms.ToolStripMenuItem(text) { Padding = new Forms.Padding(0, 4, 0, 4) };
        if (bold) mi.Font = new Drawing.Font(mi.Font, Drawing.FontStyle.Bold);
        mi.Click += (_, _) => act();
        return mi;
    }

    /// <summary>Bulle dans la barre système : une mise à jour (vérifiée) est disponible.</summary>
    public void NotifyUpdate(UpdateInfo info)
    {
        _icon.BalloonTipClicked += (_, _) => { ShowWindow(); (Application.Current.MainWindow as Views.MainWindow)?.ShowUpdateDialog(); };
        _icon.ShowBalloonTip(6000, "KB Account Manager", $"La version {info.VersionText} est disponible. Clique ici pour l'installer.", Forms.ToolTipIcon.Info);
    }

    public void ShowWindow()
    {
        _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Topmost = true; _win.Topmost = false;   // force le passage au premier plan
        _win.Focus();
    }

    /// <summary>Appelé quand la fenêtre est masquée par la croix : explique une fois où est passée l'appli.</summary>
    public void NotifyHidden()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "KB Account Manager", "L'appli continue en arrière-plan. Clic droit sur l'icône pour la quitter.", Forms.ToolTipIcon.Info);
    }

    private void UpdateText()
    {
        int n = _app.RunningCount;
        var t = n == 0 ? "KB Account Manager" : $"KB Manager — {n} en jeu";
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static Drawing.Color C(int r, int g, int b) => Drawing.Color.FromArgb(r, g, b);
        public override Drawing.Color ToolStripDropDownBackground => C(0x1A, 0x1D, 0x24);
        public override Drawing.Color ImageMarginGradientBegin => C(0x1A, 0x1D, 0x24);
        public override Drawing.Color ImageMarginGradientMiddle => C(0x1A, 0x1D, 0x24);
        public override Drawing.Color ImageMarginGradientEnd => C(0x1A, 0x1D, 0x24);
        public override Drawing.Color MenuBorder => C(0x2A, 0x2E, 0x39);
        public override Drawing.Color MenuItemBorder => C(0x2D, 0x32, 0x3F);
        public override Drawing.Color MenuItemSelected => C(0x2D, 0x32, 0x3F);
        public override Drawing.Color MenuItemSelectedGradientBegin => C(0x2D, 0x32, 0x3F);
        public override Drawing.Color MenuItemSelectedGradientEnd => C(0x2D, 0x32, 0x3F);
        public override Drawing.Color SeparatorDark => C(0x2A, 0x2E, 0x39);
        public override Drawing.Color SeparatorLight => C(0x2A, 0x2E, 0x39);
        public override Drawing.Color CheckBackground => C(0x18, 0x6B, 0xA8);
        public override Drawing.Color CheckSelectedBackground => C(0x18, 0x6B, 0xA8);
    }
}
