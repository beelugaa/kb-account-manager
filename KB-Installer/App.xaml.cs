using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KBSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "kb-setup.log"), "CRASH : " + ex.Exception + Environment.NewLine); } catch { }
            MessageBox.Show("Erreur inattendue pendant l'installation : " + ex.Exception.Message, "KB Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // Installation silencieuse : KB-Account-Manager-Setup.exe /S [/D=dossier] [/nodesktop] [/nostartmenu] [/autostart] [/launch]
        var args = e.Args.Select(a => a.Trim()).ToList();
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (Has("/S"))
        {
            var dirArg = args.FirstOrDefault(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))?[3..].Trim('"');
            var log = Path.Combine(Path.GetTempPath(), "kb-setup.log");
            int code = 0;
            try
            {
                // Mise à jour lancée par l'appli elle-même : on attend qu'elle se soit fermée (jusqu'à 60 s)
                var waitArg = args.FirstOrDefault(a => a.StartsWith("/waitpid=", StringComparison.OrdinalIgnoreCase))?[9..];
                if (int.TryParse(waitArg, out var waitPid))
                {
                    try { Process.GetProcessById(waitPid).WaitForExit(60_000); } catch { /* déjà fermée */ }
                    System.Threading.Thread.Sleep(800);
                }

                var opt = new InstallOptions(dirArg ?? InstallerCore.ExistingDir() ?? InstallerCore.DefaultDir,
                    Desktop: !Has("/nodesktop"), StartMenu: !Has("/nostartmenu"), AutoStart: Has("/autostart"), Launch: Has("/launch"));
                if (InstallerCore.IsAppRunning(opt.Dir)) throw new InvalidOperationException("KB Manager est ouvert : quitte-le puis relance l'installation.");
                InstallerCore.Install(opt, (p, m) => File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {p,3}% {m}{Environment.NewLine}"));
                if (opt.Launch)
                    Process.Start(new ProcessStartInfo(Path.Combine(InstallerCore.ValidateDir(opt.Dir), "KBManager.exe")) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, "ERREUR : " + ex.Message + Environment.NewLine);
                code = 1;
                if (Has("/showerrors"))
                    MessageBox.Show("La mise à jour de KB Account Manager a échoué : " + ex.Message + "\n\nTes comptes sont intacts. Relance l'appli, ou réinstalle avec l'installateur.",
                        "KB Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}
