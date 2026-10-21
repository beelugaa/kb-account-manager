namespace BBManager.Services;

/// <summary>
/// Plusieurs fenêtres Roblox en même temps.
///
/// Les clients Roblox 0.743+ ont une logique de « remplacement » : un nouveau client signale l'événement nommé
/// « ROBLOX_singletonEvent » au client déjà ouvert, qui se ferme, puis attend le mutex « ROBLOX_singletonMutex » (« waitForNewPlayerProcess »).
/// Constaté dans les journaux : sans rien faire, lancer un 2e compte FERME le 1er.
///
/// Un nom d'objet noyau ne peut désigner qu'un seul type d'objet. En créant un MUTEX qui porte le nom de l'événement,
/// Roblox ne peut plus créer son événement : il saute toute la logique de remplacement (0 attente, 0 signal de fermeture, mesuré)
/// et chaque client reste ouvert. On ne doit PAS tenir « ROBLOX_singletonMutex » : les clients attendraient indéfiniment qu'il se libère.
/// Le mutex doit rester possédé par un thread vivant (le thread UI) tant que l'appli tourne.
/// </summary>
public static class MultiInstance
{
    private const string Name = "ROBLOX_singletonEvent";
    private static Mutex? _mutex;

    public static bool IsActive => _mutex != null;

    /// <summary>Retourne false si un Roblox déjà ouvert (lancé hors de l'appli) détient déjà ce nom : il faut le fermer.</summary>
    public static bool Enable()
    {
        if (_mutex != null) return true;
        try
        {
            var m = new Mutex(true, Name, out var created);
            if (!created)
            {
                // Le mutex existe déjà : reste d'une ancienne session de l'appli (abandonné à sa fermeture) ? On le reprend.
                bool owned;
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { m.Dispose(); return false; }
            }
            _mutex = m;
            return true;
        }
        catch
        {
            // Un Roblox a déjà créé un ÉVÉNEMENT de ce nom (le nom est pris par un autre type d'objet) : multi-instance impossible tant qu'il tourne.
            return false;
        }
    }

    public static void Disable()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose(); _mutex = null;
    }
}
