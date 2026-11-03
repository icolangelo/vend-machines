using System.Text.RegularExpressions;

namespace VendingMachines.Api.Services;

public static partial class ReleaseMetadata
{
    public static string? GetSourceCommit(string? value)
    {
        var revision = value?.Trim();
        return revision is not null && SourceCommitPattern().IsMatch(revision)
            ? revision.ToLowerInvariant()
            : null;
    }

    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SourceCommitPattern();
}
