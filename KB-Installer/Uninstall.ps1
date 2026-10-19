param([switch]$Silent, [switch]$KeepData)
# Désinstalleur de KB Account Manager (installation par utilisateur : rien dans Program Files, aucun droit administrateur)
Add-Type -AssemblyName System.Windows.Forms
$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$name = "KB Account Manager"

function Ask($text, $buttons, $icon, $default = 'Button1') {
    if ($Silent) { return 'Yes' }
    return [System.Windows.Forms.MessageBox]::Show($text, $name, $buttons, $icon, $default).ToString()
}

if ((Ask "Désinstaller $name ?" 'YesNo' 'Question') -ne 'Yes') { exit 0 }

# L'appli de ce dossier tourne-t-elle ? (son exécutable est alors verrouillé ; une autre copie ailleurs ne compte pas)
$running = $false
try { $fs = [IO.File]::Open((Join-Path $dir 'KBManager.exe'), 'Open', 'ReadWrite', 'None'); $fs.Close() } catch [System.IO.IOException] { $running = $true } catch { }
if ($running) {
    if (-not $Silent) {
        [void][System.Windows.Forms.MessageBox]::Show("$name est ouvert. Quitte-le d'abord (icône de la barre système > Quitter), puis relance la désinstallation.`n`nTes Roblox ouverts ne sont pas fermés.", $name, 'OK', 'Information')
    }
    exit 1
}

$deleteData = $false
if (-not $KeepData -and -not $Silent) {
    $deleteData = (Ask "Supprimer aussi tes comptes et réglages enregistrés ?`n(%APPDATA%\BBManager)`n`nChoisis « Non » pour pouvoir réinstaller sans tout perdre." 'YesNo' 'Warning' 'Button2') -eq 'Yes'
}

# Raccourcis
foreach ($p in @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"))) {
    if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
}

# Démarrage avec Windows + entrée « Applications installées »
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'KBAccountManager' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KBAccountManager' -Recurse -Force -ErrorAction SilentlyContinue

# Données (si demandé), en écrasant les fichiers
if ($deleteData) {
    $data = Join-Path $env:APPDATA 'BBManager'
    if (Test-Path $data) {
        Get-ChildItem $data -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $len = $_.Length
                if ($len -gt 0) { $b = New-Object byte[] ([Math]::Min($len, 4MB)); (New-Object System.Random).NextBytes($b); [IO.File]::WriteAllBytes($_.FullName, $b) }
            } catch { }
        }
        Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Le dossier d'installation contient ce script : on le supprime juste après la fin du script
$cmd = 'ping 127.0.0.1 -n 3 >nul & rmdir /s /q "' + $dir + '"'
Start-Process -FilePath cmd.exe -ArgumentList '/c', $cmd -WindowStyle Hidden
if (-not $Silent) { [void][System.Windows.Forms.MessageBox]::Show("$name a été désinstallé.", $name, 'OK', 'Information') }
