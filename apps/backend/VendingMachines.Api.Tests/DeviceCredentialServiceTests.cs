using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeviceCredentialServiceTests
{
    [Fact]
    public void GeneratedCredentialCanBeVerifiedWithoutPersistingPlaintext()
    {
        var credential = DeviceCredentialService.Generate();
        var hash = DeviceCredentialService.Hash(credential);

        Assert.StartsWith("vmc_", credential);
        Assert.NotEqual(credential, hash);
        Assert.True(DeviceCredentialService.Verify(credential, hash));
        Assert.False(DeviceCredentialService.Verify(credential + "x", hash));
    }

    [Fact]
    public void InvalidOrMalformedHashesAreRejected()
    {
        Assert.False(DeviceCredentialService.Verify("vmc_test", null));
        Assert.False(DeviceCredentialService.Verify("vmc_test", "not-a-hash"));
        Assert.False(DeviceCredentialService.Verify("", DeviceCredentialService.Hash("vmc_test")));
    }

    [Fact]
    public void RevokedCredentialIsRejectedEvenWhenItsSecretStillMatchesTheStoredHash()
    {
        const string credential = "vmc_test";
        var hash = DeviceCredentialService.Hash(credential);

        Assert.True(DeviceCredentialService.Verify(credential, hash, revokedAt: null));
        Assert.False(DeviceCredentialService.Verify(credential, hash, DateTime.UtcNow));
    }
}
