# KB Account Manager

Gestionnaire de comptes Roblox pour Windows (WPF / .NET 8) : multi-instance, limite de RAM par fenêtre,
groupes, déblocage des FPS, rangement des fenêtres.

## Installer

- **`E:\BB\Installateur\KB-Account-Manager-Setup.exe`** : installe l'appli pour ton compte Windows (aucun droit administrateur), avec raccourcis,
  démarrage avec Windows optionnel et entrée dans « Applications installées ». Installation silencieuse : `/S [/D=dossier] [/nodesktop] [/nostartmenu] [/autostart]`.
- Reconstruire l'installateur : `python E:\BB\KB-Installeruild.py` (nécessite le runtime .NET 8 Desktop sur le PC cible).
- Sans installer : `dotnet publish -c Release -r win-x64 --self-contained false -o dist2`, puis `dist2\KBManager.exe`.

## Sécurité

- Cookies chiffrés sur le disque (DPAPI, ou AES-256-GCM + PBKDF2 600 000 itérations avec mot de passe). L'en-tête du fichier est authentifié : toute modification est détectée.
- Au premier lancement, l'appli propose un mot de passe. « J'ai oublié mon mot de passe » propose *Non* ou *Réinitialiser l'appli* (suppression totale, avec confirmation).
- Verrouillage manuel (Ctrl+L), après inactivité, ou quand la fenêtre est masquée ; essais de mot de passe ralentis après 4 erreurs.
- Les cookies ne partent que vers du HTTPS *.roblox.com (redirections interdites). Le client Roblox n'est lancé que s'il est signé par Roblox Corporation.
- Copier un cookie ou exporter demande le mot de passe ; le presse-papiers est exclu de l'historique Win+V et vidé après 30 s. Export chiffré (.kbx).
- Processus durci : les autres programmes de la session ne peuvent ni lire ni écrire sa mémoire (tu peux toujours le fermer).

## Utilisation rapide

1. **Ferme tous les Roblox**, puis ouvre KB Manager (le multi-instance doit prendre le verrou avant Roblox).
2. **Ajouter** → colle des cookies `.ROBLOSECURITY` (un par ligne, import en masse possible) ou connecte-toi via le navigateur intégré.
3. Mets les comptes dans des **groupes** (pastilles en haut, glisser-déposer des comptes dessus).
4. Choisis le jeu (Place ID ou lien), sélectionne des comptes, **Lancer**.
5. Onglet **Instances** : RAM en direct, limite par fenêtre, ranger en grille, tout fermer.
6. Onglet **Paramètres** : FPS, limite RAM globale, priorité/cœurs CPU, mode fenêtré, délais, mot de passe maître…

## Barre système, démarrage avec Windows, reprise

- La croix **réduit dans la barre système** (réglable) ; clic droit sur l'icône pour quitter. Relancer l'exe rouvre la fenêtre existante.
- **Paramètres > Application** : démarrer avec Windows (clé `Run` de ta session, démarrage réduit possible).
- **Reprise des instances** : quitter ou redémarrer l'appli ne ferme pas les Roblox ; ils sont reconnus au démarrage (PID + heure de démarrage).
  Les Roblox lancés hors de l'appli ne peuvent pas être rattachés à un compte : ils sont comptés comme « non reconnus ».

## Profils, actions groupées, mises à jour

- **Profils** (onglet dédié) : des comptes + un jeu + des options (limite de RAM, ranger les fenêtres), relançables en un clic ou **programmés** (heure + jours). « ＋ Profil » dans l'onglet Comptes crée un profil depuis la sélection.
- **Actions groupées** : sélectionne plusieurs comptes puis « Modifier la sélection… » (groupe, jeu par défaut, RAM, relance auto, anti-AFK, alias numérotés « Farm {n} », notes).
- **Mises à jour** : l'appli lit un petit fichier `update.json`, vérifie sa **signature** (clé publique intégrée) et l'empreinte SHA-256 de l'installateur, puis s'installe et se relance. Carte « Mise à jour disponible » dans la barre latérale + bulle dans la barre système.
  Publier une version : `python E:\BB\KB-Installer\release.py --feed <URL de update.json> --url <URL de l'installateur> --bump minor --notes "…"`,
  puis mettre en ligne les deux fichiers de `E:\BB\Installateur`. La clé privée (`KB-Installer\keys\update-private.pem`) signe chaque version : **ne la partage jamais et sauvegarde-la**.

## Où sont les données ?

`%APPDATA%\BBManager\` — `data.bbm` (comptes + cookies, chiffré DPAPI ou AES-256-GCM avec mot de passe maître),
`settings.json` (réglages, non sensible), `crash.log` si l'appli plante.

## Limite de RAM : 3 méthodes

| Méthode | Effet | Risque |
|---|---|---|
| Plafond permanent (défaut) | Windows interdit à la fenêtre de dépasser la limite en RAM physique, le surplus passe sur disque | saccades si la limite est trop basse (< ~1 Go) |
| Léger | l'appli vide la RAM qui dépasse, par à-coups | Roblox la regonfle vite : approximatif |
| Strict (Job Object) | plafond mémoire imposé | Roblox peut planter |

## Notes

- Le déblocage FPS écrit `DFIntTaskSchedulerTargetFps` dans `ClientAppSettings.json` (version courante du client) ; il est ré-écrit à chaque lancement, donc survit aux mises à jour de Roblox.
- Roblox peut changer son client à tout moment : si le multi-instance ou le lancement cesse de fonctionner après une mise à jour, c'est de ce côté qu'il faut regarder.
- `--demo` lance l'appli avec des données factices (rien n'est écrit sur le disque).
