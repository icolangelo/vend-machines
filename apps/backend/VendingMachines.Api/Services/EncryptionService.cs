using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VendingMachines.Api.Services;

public static class EncryptionService
{
    private static readonly byte[] KeyBytes;

    static EncryptionService()
    {
        // Chave de criptografia padrão. Em produção deve vir do appsettings / variáveis de ambiente.
        var keyString = "SuperSecretEncryptionKeyForVending123!";
        using var sha256 = SHA256.Create();
        KeyBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyString));
    }

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        using var aes = Aes.Create();
        aes.Key = KeyBytes;
        aes.GenerateIV();
        var iv = aes.IV;

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        
        // Escreve o IV no início do stream criptografado
        ms.Write(iv, 0, iv.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = KeyBytes;

            var iv = new byte[aes.BlockSize / 8];
            var cipher = new byte[fullCipher.Length - iv.Length];

            Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(cipher);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            return sr.ReadToEnd();
        }
        catch (Exception)
        {
            // Retorna o valor original caso ocorra falha de decriptação (ex: dado não criptografado)
            return cipherText;
        }
    }
}
