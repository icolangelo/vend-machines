using System.Security.Cryptography;
using System.Text;

namespace VendingMachines.Api.Services;

public static class EncryptionService
{
    public const string CurrentCiphertextPrefix = "enc:v1:";
    private const string DevelopmentKey = "DevelopmentOnlyEncryptionKeyForVending2026!";
    private static EncryptionKeys _keys = CreateKeys(DevelopmentKey, null);

    public static void Configure(
        string? currentKey,
        string? legacyKey,
        bool isDevelopment,
        bool writeLegacyFormat = false,
        bool requireLegacyData = true)
    {
        if (string.IsNullOrWhiteSpace(currentKey))
        {
            if (!isDevelopment)
                throw new InvalidOperationException("A configuração Encryption:Key é obrigatória fora do ambiente de desenvolvimento.");
            currentKey = DevelopmentKey;
        }

        if (!isDevelopment && IsPlaceholder(currentKey))
            throw new InvalidOperationException("Encryption:Key não pode conter um valor de exemplo.");

        if (!isDevelopment && requireLegacyData && string.IsNullOrWhiteSpace(legacyKey))
            throw new InvalidOperationException("A configuração Encryption:LegacyKey é obrigatória até que os dados antigos sejam recriptografados.");
        if (writeLegacyFormat && string.IsNullOrWhiteSpace(legacyKey))
            throw new InvalidOperationException("Encryption:LegacyKey é obrigatória enquanto Encryption:WriteLegacyFormat estiver habilitada.");
        if (!isDevelopment && legacyKey != null && IsPlaceholder(legacyKey))
            throw new InvalidOperationException("Encryption:LegacyKey não pode conter um valor de exemplo.");

        var currentKeyBytes = DeriveKey(currentKey, "Encryption:Key");
        var legacyKeyBytes = string.IsNullOrWhiteSpace(legacyKey)
            ? null
            : DeriveKey(legacyKey, "Encryption:LegacyKey");

        var keysMatch = legacyKeyBytes != null && CryptographicOperations.FixedTimeEquals(currentKeyBytes, legacyKeyBytes);
        if (!isDevelopment && !writeLegacyFormat && keysMatch)
            throw new InvalidOperationException("Encryption:Key deve ser diferente de Encryption:LegacyKey durante a rotação.");
        if (writeLegacyFormat && !keysMatch)
            throw new InvalidOperationException("Encryption:Key e Encryption:LegacyKey devem corresponder enquanto o formato legado for escrito.");

        Volatile.Write(ref _keys, new EncryptionKeys(currentKeyBytes, legacyKeyBytes, writeLegacyFormat, requireLegacyData));
    }

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        var keys = Volatile.Read(ref _keys);
        if (keys.WriteLegacyFormat)
            return EncryptLegacyWithKey(plainText, keys.Legacy!);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(keys.Current, tag.Length))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length + tag.Length, cipherBytes.Length);
        return CurrentCiphertextPrefix + Convert.ToBase64String(payload);
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        var keys = Volatile.Read(ref _keys);

        if (cipherText.StartsWith(CurrentCiphertextPrefix, StringComparison.Ordinal))
        {
            if (TryDecryptVersioned(cipherText[CurrentCiphertextPrefix.Length..], keys.Current, out var currentPlainText))
                return currentPlainText;
            throw new CryptographicException("Não foi possível descriptografar um valor com o formato de chave atual.");
        }

        if (cipherText.StartsWith("enc:", StringComparison.Ordinal))
            throw new CryptographicException("Formato de criptografia desconhecido.");

        // Valores anteriores ao formato versionado usavam o segredo legado. O fallback
        // para texto puro mantém compatibilidade com integrações antigas não criptografadas.
        if (keys.Legacy != null && TryDecryptWithKey(cipherText, keys.Legacy, out var legacyPlainText))
            return legacyPlainText;
        if (TryDecryptWithKey(cipherText, keys.Current, out var oldCurrentPlainText))
            return oldCurrentPlainText;
        if (keys.RequireLegacyData)
            return cipherText;
        throw new CryptographicException("Foi encontrado um valor legado após a conclusão da migração de cifra.");
    }

    public static string Reencrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.StartsWith(CurrentCiphertextPrefix, StringComparison.Ordinal))
        {
            _ = Decrypt(value);
            return value;
        }

        var keys = Volatile.Read(ref _keys);
        if (keys.WriteLegacyFormat)
            throw new InvalidOperationException("A recriptografia exige Encryption:WriteLegacyFormat=false.");
        if (value.StartsWith("enc:", StringComparison.Ordinal))
            throw new CryptographicException("Formato de criptografia desconhecido.");

        if (keys.Legacy != null && TryDecryptWithKey(value, keys.Legacy, out var legacyPlainText))
            return Encrypt(legacyPlainText);
        if (TryDecryptWithKey(value, keys.Current, out var currentPlainText))
            return Encrypt(currentPlainText);
        if (LooksLikeLegacyCiphertext(value))
            throw new CryptographicException("Um ciphertext legado não pôde ser lido; nenhum dado foi alterado.");
        if (!keys.RequireLegacyData)
            throw new CryptographicException("Um valor sem prefixo foi encontrado após a conclusão da migração de cifra.");

        // Dados que eram texto puro também são migrados para o formato autenticado.
        return Encrypt(value);
    }

    private static string EncryptLegacyWithKey(string plainText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var output = new MemoryStream();
        output.Write(aes.IV, 0, aes.IV.Length);
        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(aes.Key, aes.IV), CryptoStreamMode.Write))
        using (var writer = new StreamWriter(crypto))
            writer.Write(plainText);
        return Convert.ToBase64String(output.ToArray());
    }

    private static bool LooksLikeLegacyCiphertext(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            const int ivLength = 16;
            return bytes.Length >= ivLength + 16 && (bytes.Length - ivLength) % 16 == 0;
        }
        catch (FormatException) { return false; }
    }

    private static bool TryDecryptVersioned(string cipherText, byte[] key, out string plainText)
    {
        plainText = string.Empty;
        try
        {
            var payload = Convert.FromBase64String(cipherText);
            const int nonceLength = 12;
            const int tagLength = 16;
            if (payload.Length < nonceLength + tagLength) return false;

            var nonce = payload.AsSpan(0, nonceLength);
            var tag = payload.AsSpan(nonceLength, tagLength);
            var cipher = payload.AsSpan(nonceLength + tagLength);
            var plaintextBytes = new byte[cipher.Length];
            using var aes = new AesGcm(key, tagLength);
            aes.Decrypt(nonce, cipher, tag, plaintextBytes);
            plainText = Encoding.UTF8.GetString(plaintextBytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryDecryptWithKey(string cipherText, byte[] key, out string plainText)
    {
        plainText = string.Empty;
        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);
            using var aes = Aes.Create();
            var ivLength = aes.BlockSize / 8;
            if (fullCipher.Length <= ivLength) return false;

            aes.Key = key;
            aes.IV = fullCipher.AsSpan(0, ivLength).ToArray();
            using var input = new MemoryStream(fullCipher, ivLength, fullCipher.Length - ivLength);
            using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var reader = new StreamReader(crypto, Encoding.UTF8);
            plainText = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private static EncryptionKeys CreateKeys(string currentKey, string? legacyKey) =>
        new(DeriveKey(currentKey, "Encryption:Key"), legacyKey == null ? null : DeriveKey(legacyKey, "Encryption:LegacyKey"), false, true);

    private static byte[] DeriveKey(string value, string settingName)
    {
        if (Encoding.UTF8.GetByteCount(value) < 32)
            throw new InvalidOperationException($"{settingName} precisa ter pelo menos 32 bytes.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static bool IsPlaceholder(string value) =>
        new[] { "change_me", "replace_with", "your_", "placeholder", "example", "dummy", "cole_aqui" }
            .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed record EncryptionKeys(byte[] Current, byte[]? Legacy, bool WriteLegacyFormat, bool RequireLegacyData);
}
