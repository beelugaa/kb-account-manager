using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KBSetup;

public partial class MainWindow : Window
{
    private string? _installedDir;
    private bool _done;
    private readonly DispatcherTimer _runningCheck = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow()
    {
        InitializeComponent();
        _installedDir = InstallerCore.ExistingDir();
        DirBox.Text = _installedDir ?? InstallerCore.DefaultDir;
        VersionText.Text = $"Version {InstallerCore.Version}";

        if (_installedDir != null)
        {
            TitleText.Text = "Mettre à jour KB Account Manager";
            InstallBtn.Content = "Mettre à jour";
            DirHint.Text = "Une installation existe déjà : ses fichiers seront remplacés. Tes comptes et réglages sont conservés.";
        }

        _runningCheck.Tick += (_, _) => UpdateRunning();
        _runningCheck.Start();
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        if (_done) return;
        bool running = InstallerCore.IsAppRunning(DirBox.Text);
        RunningWarn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.IsEnabled = !running && PageOptions.Visibility == Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier d'installation", InitialDirectory = Path.GetDirectoryName(DirBox.Text) };
        if (dlg.ShowDialog(this) == true) DirBox.Text = dlg.FolderName;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_done) { Finish(); return; }

        ErrText.Visibility = Visibility.Collapsed;
        InstallOptions opt;
        try
        {
            var dir = InstallerCore.ValidateDir(DirBox.Text);
            opt = new InstallOptions(dir, DesktopBox.IsChecked == true, MenuBox.IsChecked == true, AutoStartBox.IsChecked == true, false);
        }
        catch (Exception ex) { ShowError(ex.Message); return; }

        PageOptions.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;
        InstallBtn.IsEnabled = false; CancelBtn.IsEnabled = false;
        _runningCheck.Stop();

        var progress = new Progress<(int pct, string msg)>(p => { Bar.Value = p.pct; ProgDetail.Text = p.msg; });
        try
        {
            await Task.Run(() => InstallerCore.Install(opt, (p, m) => ((IProgress<(int, string)>)progress).Report((p, m))));
        }
        catch (Exception ex)
        {
            PageProgress.Visibility = Visibility.Collapsed;
            PageOptions.Visibility = Visibility.Visible;
            CancelBtn.IsEnabled = true; InstallBtn.IsEnabled = true;
            ShowError("L'installation a échoué : " + ex.Message);
            _runningCheck.Start();
            return;
        }

        _done = true;
        _installedDir = opt.Dir;
        PageProgress.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Visible;
        DoneText.Text = $"Installé dans {opt.Dir}";
        InstallBtn.Content = "Terminer"; InstallBtn.IsEnabled = true;
        CancelBtn.Visibility = Visibility.Collapsed;
        TitleText.Text = "Installation terminée";
    }

    private void Finish()
    {
        if (LaunchBox.IsChecked == true && _installedDir != null)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(_installedDir, "KBManager.exe")) { UseShellExecute = true, WorkingDirectory = _installedDir }); }
            catch (Exception ex) { MessageBox.Show(this, "Impossible de lancer l'appli : " + ex.Message, "KB Account Manager"); }
        }
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string msg) { ErrText.Text = msg; ErrText.Visibility = Visibility.Visible; }
}
