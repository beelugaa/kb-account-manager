using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BBManager.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run) : this(_ => run()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

public static class Util
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0} Ko";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0} Mo";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} Go";
    }

    public static string FormatSpan(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    private static bool _hardened;

    /// <summary>Le dossier de données n'est accessible qu'à toi, à SYSTEM et aux administrateurs (aucun héritage).</summary>
    private static void Harden(string dir)
    {
        if (_hardened) return;
        _hardened = true;
        try
        {
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Allow(System.Security.Principal.SecurityIdentifier sid) => sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                sid, System.Security.AccessControl.FileSystemRights.FullControl, inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Allow(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            Allow(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
            Allow(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
            new System.IO.DirectoryInfo(dir).SetAccessControl(sec);
        }
        catch { /* au pire on garde les droits hérités du profil utilisateur */ }
    }

    public static string AppDir
    {
        get
        {
            // KBM_DATA : dossier de données alternatif (mode portable / tests)
            var d = Environment.GetEnvironmentVariable("KBM_DATA") is { Length: > 0 } custom
                ? custom
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BBManager");
            System.IO.Directory.CreateDirectory(d);
            Harden(d);
            return d;
        }
    }
}
