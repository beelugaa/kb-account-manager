# Publier KB Account Manager et ses mises à jour (GitHub)

## Une seule fois : créer le dépôt
1. Crée un compte sur https://github.com (gratuit) si ce n'est pas déjà fait.
2. Clique **New repository** → nom (par exemple `kb-account-manager`) → **Public** → coche « Add a README » → **Create repository**.
   - Le dépôt doit être **public** : c'est ce qui permet à l'appli de ton ami de télécharger la mise à jour sans compte.
   - Tu n'as **rien d'autre** à y mettre que ces fichiers de release : ton code reste chez toi.
3. Donne-moi `ton-pseudo/kb-account-manager`.

## À chaque nouvelle version
```
python E:\BB\KB-Installer\release.py --github ton-pseudo/kb-account-manager --bump minor --notes "Ce qui change"
```
(`--bump patch` pour une petite correction, `minor` pour des nouveautés, `major` pour un gros changement.)

Le script construit l'installateur, le signe, puis t'affiche les 4 étapes pour la page « New release » de GitHub :
tu y glisses **deux fichiers** (`KB-Account-Manager-Setup.exe` et `update.json`) et tu cliques **Publish release**.

## Ce que fait l'appli de ton ami
- Elle regarde toutes les 6 h (et 20 s après son démarrage) s'il y a une version plus récente.
- Si oui : une carte **Mise à jour disponible** apparaît à gauche + une bulle dans la barre système. Un clic → installation → l'appli se rouvre.
- Elle n'installe QUE ce qui est signé avec ta clé : un faux fichier posé sur GitHub serait refusé.

## Première installation chez ton ami
Envoie-lui `E:\BB\Installateur\KB-Account-Manager-Setup.exe` **construit avec la commande ci-dessus** (c'est ce qui grave l'adresse GitHub dans l'appli).
Windows peut afficher « SmartScreen » car l'installateur n'est pas signé par un éditeur payant : « Informations complémentaires » → « Exécuter quand même ».

## Ta clé de signature
`E:\BB\KB-Installer\keys\update-private.pem` : ne la partage jamais (ni GitHub, ni Discord) et fais-en une copie.
Perdue = les appis déjà installées ne pourront plus se mettre à jour toutes seules.
