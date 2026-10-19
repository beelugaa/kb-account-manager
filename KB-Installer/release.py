"""Publie une nouvelle version : construit l'installateur, le signe avec TA clé privée, et prépare les fichiers à mettre en ligne.

  python release.py --feed https://exemple.com/update.json --url https://exemple.com/KB-Account-Manager-Setup.exe
                    [--bump patch|minor|major] [--notes "Ce qui change"]

--feed : adresse FIXE du fichier update.json (gravée dans l'appli ; elle ne change plus ensuite)
--url  : adresse où sera téléchargé l'installateur de CETTE version
Résultat dans E:\\BB\\Installateur : KB-Account-Manager-Setup.exe + update.json  (à mettre en ligne aux adresses ci-dessus)
"""
import argparse, base64, hashlib, json, os, re, subprocess, sys
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

ROOT = os.path.dirname(os.path.abspath(__file__))
APP_CSPROJ = os.path.join(ROOT, '..', 'BBManager', 'BBManager.csproj')
SETUP_CSPROJ = os.path.join(ROOT, 'KB-Installer.csproj')
FEED_FILE = os.path.join(ROOT, '..', 'BBManager', 'Assets', 'update-feed.txt')
KEY = os.path.join(ROOT, 'keys', 'update-private.pem')
OUT = os.path.join(ROOT, '..', 'Installateur')

ap = argparse.ArgumentParser()
ap.add_argument('--github', help="utilisateur/depot GitHub : calcule seul les adresses du flux et de l'installateur")
ap.add_argument('--feed')
ap.add_argument('--url')
ap.add_argument('--bump', choices=['patch', 'minor', 'major'])
ap.add_argument('--notes', default='')
ap.add_argument('--publish', action='store_true', help='publie directement la release sur GitHub avec gh (outil connecté à ton compte)')
a = ap.parse_args()
if not a.github and not (a.feed and a.url):
    sys.exit('Indique --github utilisateur/depot, ou bien --feed ET --url.')

def version_of(path):
    return re.search(r'<Version>([\d.]+)</Version>', open(path, encoding='utf-8').read()).group(1)

def set_version(path, v):
    s = open(path, encoding='utf-8').read()
    open(path, 'w', encoding='utf-8').write(re.sub(r'<Version>[\d.]+</Version>', f'<Version>{v}</Version>', s))

ver = version_of(APP_CSPROJ)
if a.bump:
    p = [int(x) for x in ver.split('.')]
    while len(p) < 3: p.append(0)
    if a.bump == 'major': p = [p[0] + 1, 0, 0]
    elif a.bump == 'minor': p = [p[0], p[1] + 1, 0]
    else: p = [p[0], p[1], p[2] + 1]
    ver = '.'.join(map(str, p))
    set_version(APP_CSPROJ, ver)
    set_version(SETUP_CSPROJ, ver)
print(f'Version : {ver}')
if a.github:
    a.feed = f'https://github.com/{a.github}/releases/latest/download/update.json'
    a.url = f'https://github.com/{a.github}/releases/download/v{ver}/KB-Account-Manager-Setup.exe'

if not os.path.exists(KEY):
    sys.exit('Clé privée introuvable : ' + KEY)

# 1) l'adresse du flux est gravée dans l'appli avant la compilation
open(FEED_FILE, 'w').write(a.feed.strip())

# 2) construction de l'installateur (appli + installateur)
if subprocess.run([sys.executable, os.path.join(ROOT, 'build.py')], cwd=ROOT).returncode != 0:
    sys.exit('Échec de la construction.')

setup = os.path.join(OUT, 'KB-Account-Manager-Setup.exe')
data = open(setup, 'rb').read()
sha = hashlib.sha256(data).hexdigest()

# 3) signature : KBM-UPDATE|version|sha256|url  (ECDSA P-256 / SHA-256, DER)
key = serialization.load_pem_private_key(open(KEY, 'rb').read(), password=None)
message = f'KBM-UPDATE|{ver}|{sha}|{a.url}'.encode()   # même texte que l'appli (Version.ToString)
signature = base64.b64encode(key.sign(message, ec.ECDSA(hashes.SHA256()))).decode()

feed = {'version': ver, 'url': a.url, 'sha256': sha, 'signature': signature, 'notes': a.notes}
json.dump(feed, open(os.path.join(OUT, 'update.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=2)

print(f'\nSHA-256 : {sha}')
print('\nÀ METTRE EN LIGNE :')
if a.github:
    print(f'  1) Ouvre https://github.com/{a.github}/releases/new')
    print(f'  2) Tag : v{ver}   (titre : KB Account Manager {ver})')
    print('  3) Glisse-dépose ces DEUX fichiers dans « Attach binaries » :')
    print(f'       {os.path.abspath(setup)}')
    print(f'       {os.path.abspath(os.path.join(OUT, "update.json"))}')
    print('  4) Clique « Publish release ». Les appis installées verront la mise à jour dans les 6 h (ou via Paramètres > Vérifier maintenant).')
else:
    print(f'  {os.path.abspath(setup)}  ->  {a.url}')
    print(f'  {os.path.abspath(os.path.join(OUT, "update.json"))}  ->  {a.feed}')

if a.publish and a.github:
    gh = os.path.join(ROOT, '..', 'tools', 'gh', 'bin', 'gh.exe')
    r = subprocess.run([gh, 'release', 'create', f'v{ver}', setup, os.path.join(OUT, 'update.json'), '--repo', a.github,
                        '--title', f'KB Account Manager {ver}', '--notes', a.notes or f'Version {ver}'])
    print('\nPubliée sur GitHub.' if r.returncode == 0 else '\nÉCHEC de la publication (gh est-il connecté ? gh auth status).')
