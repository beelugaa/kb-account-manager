using System.Windows;
using BBManager.Services;

namespace BBManager.Views;

/// <summary>Annonce une mise à jour vérifiée, la télécharge, contrôle son empreinte, puis lance l'installateur.</summary>
public partial class UpdateDialog : Window
{
    private readonly AppState _app;
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;

    public UpdateDialog(AppState app, UpdateInfo info)
    {
        InitializeComponent();
        _app = app; _info = info;
        VersionText.Text = $"Version {info.VersionText}   (tu as la {UpdateService.CurrentText})";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "Corrections et améliorations." : info.Notes;
        Closing += (_, _) => _cts?.Cancel();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false; LaterBtn.Content = "Annuler";
        ErrText.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible; Bar.Value = 0;
        InfoText.Text = "Téléchargement et vérification de la signature…";
        _cts = new CancellationTokenSource();
        try
        {
            var path = await _app.Updates.DownloadAsync(_info, new Progress<int>(p => Bar.Value = p), _cts.Token);
            InfoText.Text = "Vérifié. Installation en cours : l'appli va se rouvrir…";
            await Task.Delay(600);
            _app.SaveNow();
            UpdateService.LaunchInstaller(path);
            App.Quit();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Bar.Visibility = Visibility.Collapsed;
            ErrText.Text = "La mise à jour a échoué : " + ex.Message;
            ErrText.Visibility = Visibility.Visible;
            InstallBtn.IsEnabled = true; InstallBtn.Content = "Réessayer"; LaterBtn.Content = "Fermer";
            InfoText.Text = "Rien n'a été modifié sur ton PC.";
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
