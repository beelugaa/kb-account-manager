namespace BBManager.Services;

/// <summary>
/// Plusieurs fenêtres Roblox en même temps.
///
/// Avant, l'appli prenait le verrou « ROBLOX_singletonMutex » pour que Roblox ne se ferme pas à l'ouverture d'un 2e client.
/// Depuis les clients 0.743/0.744, c'est l'inverse : chaque nouveau client ATTEND que ce verrou soit libre (« waitForNewPlayerProcess »)
/// et le client précédent ne le garde que pendant son démarrage. Si l'appli le garde en permanence, tous les clients restent
/// bloqués sur l'écran de démarrage (constaté : 23 attentes de 250 ms en 8 s, jamais de sortie). On ne touche donc plus au verrou :
/// les clients se lancent l'un après l'autre et se relayent tout seuls.
/// Ce réglage ne sert plus qu'à autoriser (ou refuser) le lancement d'un compte quand un autre est déjà ouvert.
/// </summary>
public static class MultiInstance
{
    private static bool _on;

    public static bool IsActive => _on;

    /// <summary>Toujours possible : plus aucun verrou à prendre.</summary>
    public static bool Enable() { _on = true; return true; }

    public static void Disable() => _on = false;
}
