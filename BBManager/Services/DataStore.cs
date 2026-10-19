using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBManager.Core;
using BBManager.Models;

namespace BBManager.Services;

/// <summary>
/// Stockage local. Les comptes (cookies inclus) sont toujours chiffrés :
///  - sans mot de passe : DPAPI (lié à ta session Windows)
///  - avec mot de passe maître : AES-256-GCM, clé dérivée par PBKDF2-SHA256 (600 000 itérations, sel aléatoire).
///    L'en-tête (version, itérations, sel) est authentifié : toute modification du fichier est détectée.
/// Le mot de passe n'est jamais conservé en mémoire : seule la clé dérivée l'est.
/// </summary>
public static class DataStore
{
    private static readonly byte[] Magic1 = "BBM1"u8.ToArray();   // ancien format (250 000 itérations, en-tête non authentifié)
    private static readonly byte[] Magic2 = "BBM2"u8.ToArray();
    private static readonly byte[] Entropy = "BBManager.v1"u8.ToArray();
    private const int CurrentIterations = 600_000;
    private const int LegacyIterations = 250_000;
    private const int HeaderLen = 25;                              // magic(4) mode(1) itérations(4) sel(16)

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DataPath => Path.Combine(Util.AppDir, "data.bbm");
    public static string SettingsPath => Path.Combine(Util.AppDir, "settings.json");

    private static byte[]? _key;
    private static byte[]? _salt;
    private static int _iter;
    private static bool _dropBackup;

    public static bool HasPassword => _key != null;
    public static bool DataFileExists => File.Exists(DataPath);

    /// <summary>True si le fichier de données demande un mot de passe.</summary>
    public static bool IsPasswordProtected()
    {
        if (!File.Exists(DataPath)) return false;
        using var fs = File.OpenRead(DataPath);
        var head = new byte[5];
        return fs.Read(head, 0, 5) == 5 && head[4] == 1;
    }

    // ---- Settings (non sensibles) ------------------------------------------
    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
                s.Sanitize();
                return s;
            }
        }
        catch { /* fichier corrompu ou altéré : on repart sur les valeurs par défaut */ }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        WriteAtomic(SettingsPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, Json)), keepBackup: false);
    }

    // ---- Données chiffrées -------------------------------------------------
    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var pb = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(pb, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(pb); }
    }

    public static DataFile Load(string? password)
    {
        if (!File.Exists(DataPath))
        {
            if (!string.IsNullOrEmpty(password)) SetPassword(password);
            return new DataFile();
        }

        var raw = File.ReadAllBytes(DataPath);
        if (raw.Length < 5) throw new InvalidDataException("Fichier de données invalide.");
        bool v2 = raw.AsSpan(0, 4).SequenceEqual(Magic2);
        bool v1 = raw.AsSpan(0, 4).SequenceEqual(Magic1);
        if (!v1 && !v2) throw new InvalidDataException("Fichier de données invalide.");

        byte mode = raw[4];
        byte[] plain;
        if (mode == 0)
        {
            plain = ProtectedData.Unprotect(raw[5..], Entropy, DataProtectionScope.CurrentUser);
            _key = null; _salt = null; _iter = 0;
        }
        else if (mode == 1)
        {
            if (string.IsNullOrEmpty(password)) throw new CryptographicException("Mot de passe requis.");

            int iter; byte[] salt, nonce, tag, cipher, aad;
            if (v2)
            {
                if (raw.Length < HeaderLen + 28) throw new InvalidDataException("Fichier de données tronqué.");
                iter = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(5, 4));
                salt = raw[9..25]; nonce = raw[25..37]; tag = raw[37..53]; cipher = raw[53..]; aad = raw[..HeaderLen];
            }
            else
            {
                if (raw.Length < 49) throw new InvalidDataException("Fichier de données tronqué.");
                iter = LegacyIterations;
                salt = raw[5..21]; nonce = raw[21..33]; tag = raw[33..49]; cipher = raw[49..]; aad = Array.Empty<byte>();
            }
            if (iter < 100_000 || iter > 10_000_000) throw new InvalidDataException("Paramètres de chiffrement invalides.");

            var key = Derive(password, salt, iter);
            plain = new byte[cipher.Length];
            try
            {
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, cipher, tag, plain, aad);       // CryptographicException si mauvais mot de passe ou fichier modifié
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }

            if (v2 && iter >= CurrentIterations) { _key = key; _salt = salt; _iter = iter; }
            else
            {
                // Ancien format ou itérations plus faibles : on passe au format actuel (nouveau sel) à la prochaine sauvegarde
                CryptographicOperations.ZeroMemory(key);
                SetPassword(password);
            }
        }
        else throw new InvalidDataException("Mode de chiffrement inconnu.");

        try
        {
            var data = JsonSerializer.Deserialize<DataFile>(plain, Json) ?? new DataFile();
            data.Sanitize();
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Save(DataFile data)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        byte[] output;
        try
        {
            if (_key == null || _salt == null)
            {
                var enc = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                output = Magic2.Concat(new byte[] { 0 }).Concat(enc).ToArray();
            }
            else
            {
                var header = new byte[HeaderLen];
                Magic2.CopyTo(header, 0);
                header[4] = 1;
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5, 4), _iter);
                _salt.CopyTo(header, 9);

                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var cipher = new byte[plain.Length];
                using var gcm = new AesGcm(_key, 16);
                gcm.Encrypt(nonce, plain, cipher, tag, header);     // l'en-tête est authentifié (AAD)
                output = header.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }

        WriteAtomic(DataPath, output, keepBackup: !_dropBackup);
        if (_dropBackup) { SecureDelete(DataPath + ".bak"); _dropBackup = false; }
    }

    /// <summary>Vérifie un mot de passe contre la clé en mémoire (temps constant).</summary>
    public static bool VerifyPassword(string password)
    {
        if (_key == null || _salt == null || string.IsNullOrEmpty(password)) return false;
        var k = Derive(password, _salt, _iter);
        try { return CryptographicOperations.FixedTimeEquals(k, _key); }
        finally { CryptographicOperations.ZeroMemory(k); }
    }

    /// <summary>
    /// Active, change ou retire (null) le mot de passe maître ; prend effet à la prochaine sauvegarde.
    /// L'ancienne copie de sauvegarde (.bak) est supprimée : elle contiendrait les cookies sous l'ancienne protection.
    /// </summary>
    public static void SetPassword(string? newPassword)
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        if (string.IsNullOrEmpty(newPassword)) { _key = null; _salt = null; _iter = 0; }
        else
        {
            _salt = RandomNumberGenerator.GetBytes(16);
            _iter = CurrentIterations;
            _key = Derive(newPassword, _salt, _iter);
        }
        _dropBackup = true;
        SecureDelete(DataPath + ".bak");
    }

    // ---- Réinitialisation complète -----------------------------------------
    /// <summary>Supprime toutes les données de l'appli (comptes, réglages, sauvegardes, journaux) en écrasant les fichiers.</summary>
    public static void ResetAll()
    {
        foreach (var f in new[] { DataPath, DataPath + ".bak", DataPath + ".tmp", SettingsPath, SettingsPath + ".bak", SettingsPath + ".tmp",
                                  Path.Combine(Util.AppDir, "crash.log") })
            SecureDelete(f);
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null; _salt = null; _iter = 0; _dropBackup = false;
    }

    // ---- Export chiffré (fichier .kbx) -------------------------------------
    private static readonly byte[] ExportMagic = "KBX1"u8.ToArray();

    public static byte[] EncryptBlob(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(password, salt, CurrentIterations);
        try
        {
            var header = new byte[24];
            ExportMagic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), CurrentIterations);
            salt.CopyTo(header, 8);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plain.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(nonce, plain, cipher, tag, header);
            return header.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] DecryptBlob(byte[] blob, string password)
    {
        if (blob.Length < 24 + 28 || !blob.AsSpan(0, 4).SequenceEqual(ExportMagic)) throw new InvalidDataException("Ce n'est pas un export KB Manager (.kbx).");
        int iter = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4, 4));
        if (iter < 100_000 || iter > 10_000_000) throw new InvalidDataException("Paramètres de chiffrement invalides.");
        var salt = blob[8..24]; var nonce = blob[24..36]; var tag = blob[36..52]; var cipher = blob[52..];
        var key = Derive(password, salt, iter);
        try
        {
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain, blob[..24]);
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // ---- Fichiers -----------------------------------------------------------
    private static void WriteAtomic(string path, byte[] bytes, bool keepBackup)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
        {
            if (keepBackup) File.Copy(path, path + ".bak", true);
            File.Replace(tmp, path, null);
        }
        else File.Move(tmp, path);
    }

    /// <summary>Écrase le fichier avec des octets aléatoires avant de le supprimer (au mieux : les SSD peuvent conserver des copies).</summary>
    public static void SecureDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[(int)Math.Min(Math.Max(len, 1), 1 << 20)];
                for (long left = len; left > 0;)
                {
                    RandomNumberGenerator.Fill(buf);
                    int n = (int)Math.Min(left, buf.Length);
                    fs.Write(buf, 0, n);
                    left -= n;
                }
                fs.Flush(true);
            }
            File.Delete(path);
        }
        catch { try { File.Delete(path); } catch { } }
    }
}
