"""Construit l'installateur : publie l'appli, l'archive dans payload.zip, puis publie l'installateur (un seul .exe).

Usage :  python build.py
Résultat : E:\\BB\\Installateur\\KB-Account-Manager-Setup.exe
"""
import os, shutil, subprocess, sys, zipfile, re

ROOT = os.path.dirname(os.path.abspath(__file__))
APP_PROJ = os.path.join(ROOT, '..', 'BBManager', 'BBManager.csproj')
APP_OUT = os.path.join(ROOT, 'app')
OUT_DIR = os.path.join(ROOT, 'out')
FINAL_DIR = os.path.join(ROOT, '..', 'Installateur')
PAYLOAD = os.path.join(ROOT, 'payload.zip')

def run(*cmd):
    print('>', ' '.join(cmd))
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        sys.exit(f'Échec : {" ".join(cmd)}')

# 1) l'appli
shutil.rmtree(APP_OUT, ignore_errors=True)
run('dotnet', 'publish', APP_PROJ, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', APP_OUT)

# 2) archive (sans symboles de debug, docs XML, ni les binaires natifs d'autres architectures)
SKIP_DIRS = {'win-arm64', 'win-x86'}
if os.path.exists(PAYLOAD): os.remove(PAYLOAD)
n = 0
with zipfile.ZipFile(PAYLOAD, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for folder, dirs, files in os.walk(APP_OUT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.lower().endswith(('.pdb', '.xml')) or f.startswith('Microsoft.Web.WebView2.WinForms'):
                continue
            full = os.path.join(folder, f)
            z.write(full, os.path.relpath(full, APP_OUT).replace('\\', '/'))
            n += 1
print(f'payload.zip : {n} fichiers, {os.path.getsize(PAYLOAD) / 1e6:.1f} Mo')

# 3) l'installateur
shutil.rmtree(OUT_DIR, ignore_errors=True)
run('dotnet', 'publish', os.path.join(ROOT, 'KB-Installer.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'false', '-p:PublishSingleFile=true', '-o', OUT_DIR)

os.makedirs(FINAL_DIR, exist_ok=True)
exe = os.path.join(OUT_DIR, 'KB-Account-Manager-Setup.exe')
dest = os.path.join(FINAL_DIR, 'KB-Account-Manager-Setup.exe')
shutil.copy2(exe, dest)
print(f'\nInstallateur : {os.path.abspath(dest)}  ({os.path.getsize(dest) / 1e6:.1f} Mo)')
