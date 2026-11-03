using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ReleaseMetadataTests
{
    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("unknown", null)]
    [InlineData("https://example.invalid", null)]
    [InlineData("0123456789abcdef", null)]
    public void GetSourceCommitReturnsOnlyFullHexadecimalRevisions(string? value, string? expected)
    {
        Assert.Equal(expected, ReleaseMetadata.GetSourceCommit(value));
    }
}
