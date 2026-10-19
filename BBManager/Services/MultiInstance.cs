namespace BBManager.Services;

/// <summary>
/// Roblox n'autorise qu'une instance via le mutex nommé "ROBLOX_singletonMutex".
/// En le possédant avant le client, les lancements suivants ne sont plus bloqués.
/// Le mutex doit rester possédé par un thread vivant (le thread UI) tant que l'appli tourne.
/// </summary>
public static class MultiInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _event;

    public static bool IsActive => _mutex != null;

    /// <summary>Retourne false si un Roblox déjà ouvert détient le mutex (il faut le fermer).</summary>
    public static bool Enable()
    {
        if (_mutex != null) return true;
        try
        {
            var m = new Mutex(true, "ROBLOX_singletonMutex", out var created);
            if (!created)
            {
                // Le mutex existe déjà. Si c'est une ancienne session de l'appli qui l'a laissé (abandonné à sa fermeture,
                // alors que des clients tournent encore), on peut le reprendre. S'il est tenu par un Roblox lancé hors
                // de l'appli, WaitOne échoue : le multi-instance est réellement bloqué.
                bool owned;
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { m.Dispose(); return false; }
            }
            _mutex = m;
            try { _event = new EventWaitHandle(false, EventResetMode.AutoReset, "ROBLOX_singletonEvent"); } catch { }
            return true;
        }
        catch { return false; }
    }

    public static void Disable()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose(); _mutex = null;
        _event?.Dispose(); _event = null;
    }
}
