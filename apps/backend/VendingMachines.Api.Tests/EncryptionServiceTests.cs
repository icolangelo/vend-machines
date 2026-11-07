using VendingMachines.Api.Services;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace VendingMachines.Api.Tests;

[CollectionDefinition("Encryption configuration", DisableParallelization = true)]
public sealed class EncryptionConfigurationCollection { }

[Collection("Encryption configuration")]
public sealed class EncryptionServiceTests
{
    private const string LegacyKey = "TestOnlyLegacyEncryptionKeyForUnitTests2026!";
    private const string CurrentKey = "TestOnlyCurrentEncryptionKeyForUnitTests2026!";

    [Fact]
    public void NewCiphertextIsVersionedAndRoundTripsWithCurrentKey()
    {
        try
        {
            EncryptionService.Configure(CurrentKey, null, isDevelopment: true);

            var encrypted = EncryptionService.Encrypt("mercado-pago-token");

            Assert.StartsWith("enc:v1:", encrypted);
            Assert.Equal("mercado-pago-token", EncryptionService.Decrypt(encrypted));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void LegacyCiphertextCanBeReadDuringKeyRotation()
    {
        try
        {
            var legacyCiphertext = EncryptWithLegacyFormat("persisted-access-token", LegacyKey);

            EncryptionService.Configure(CurrentKey, LegacyKey, isDevelopment: true);

            Assert.Equal("persisted-access-token", EncryptionService.Decrypt(legacyCiphertext));
            Assert.Equal("plaintext-legacy-value", EncryptionService.Decrypt("plaintext-legacy-value"));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void VersionedCiphertextFailsClosedWhenTheCurrentKeyDoesNotMatch()
    {
        try
        {
            EncryptionService.Configure(LegacyKey, null, isDevelopment: true);
            var encrypted = EncryptionService.Encrypt("persisted-access-token");
            EncryptionService.Configure(CurrentKey, LegacyKey, isDevelopment: true);

            Assert.Throws<CryptographicException>(() => EncryptionService.Decrypt(encrypted));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void VersionedCiphertextRejectsTampering()
    {
        try
        {
            EncryptionService.Configure(CurrentKey, null, isDevelopment: true);
            var encrypted = EncryptionService.Encrypt("persisted-access-token");
            var payload = Convert.FromBase64String(encrypted["enc:v1:".Length..]);
            payload[^1] ^= 0x01;

            Assert.Throws<CryptographicException>(() =>
                EncryptionService.Decrypt("enc:v1:" + Convert.ToBase64String(payload)));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void ProductionConfigurationRequiresDistinctNonPlaceholderKeys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EncryptionService.Configure(CurrentKey, null, isDevelopment: false));
        Assert.Throws<InvalidOperationException>(() =>
            EncryptionService.Configure(CurrentKey, CurrentKey, isDevelopment: false));
        Assert.Throws<InvalidOperationException>(() =>
            EncryptionService.Configure("REPLACE_WITH_A_SECURE_KEY_VALUE_123456789", LegacyKey, isDevelopment: false));
    }

    [Fact]
    public void CompatibilityReleaseCanKeepWritingTheLegacyFormat()
    {
        try
        {
            EncryptionService.Configure(
                LegacyKey,
                LegacyKey,
                isDevelopment: false,
                writeLegacyFormat: true,
                requireLegacyData: true);

            var ciphertext = EncryptionService.Encrypt("existing-provider-token");

            Assert.DoesNotContain("enc:v1:", ciphertext, StringComparison.Ordinal);
            Assert.Equal("existing-provider-token", EncryptionService.Decrypt(ciphertext));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void ReencryptMigratesLegacyAndPlaintextValuesToTheCurrentFormat()
    {
        try
        {
            EncryptionService.Configure(CurrentKey, LegacyKey, isDevelopment: true);
            var legacyCiphertext = EncryptWithLegacyFormat("stored-token", LegacyKey);

            var migratedLegacy = EncryptionService.Reencrypt(legacyCiphertext);
            var migratedPlaintext = EncryptionService.Reencrypt("old-plaintext-token");

            Assert.StartsWith(EncryptionService.CurrentCiphertextPrefix, migratedLegacy);
            Assert.StartsWith(EncryptionService.CurrentCiphertextPrefix, migratedPlaintext);
            Assert.Equal("stored-token", EncryptionService.Decrypt(migratedLegacy));
            Assert.Equal("old-plaintext-token", EncryptionService.Decrypt(migratedPlaintext));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    [Fact]
    public void CompletedMigrationModeRejectsUnversionedValues()
    {
        try
        {
            EncryptionService.Configure(
                CurrentKey,
                null,
                isDevelopment: false,
                writeLegacyFormat: false,
                requireLegacyData: false);

            Assert.Throws<CryptographicException>(() => EncryptionService.Decrypt("legacy-value"));
        }
        finally
        {
            EncryptionService.Configure(null, null, isDevelopment: true);
        }
    }

    private static string EncryptWithLegacyFormat(string value, string key)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        aes.GenerateIV();
        using var output = new MemoryStream();
        output.Write(aes.IV, 0, aes.IV.Length);
        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(aes.Key, aes.IV), CryptoStreamMode.Write))
        using (var writer = new StreamWriter(crypto))
            writer.Write(value);
        return Convert.ToBase64String(output.ToArray());
    }
}
