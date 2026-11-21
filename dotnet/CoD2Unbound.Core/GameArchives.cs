using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record GameArchiveSurvey(int Found, ImmutableArray<string> Missing)
{
    public bool IsComplete => Missing.IsEmpty;
}

public static class GameArchives
{
    public static ImmutableArray<string> Required { get; } =
        [.. Enumerable.Range(0, 16).Select(i => FormattableString.Invariant($"main/iw_{i:D2}.iwd"))];

    public static GameArchiveSurvey Survey(IGameFiles files, string root)
    {
        var missing = string.IsNullOrEmpty(root) ? Required :
            Required.Where(path => !files.IsRegularFile(root, path)).ToImmutableArray();
        return new(Required.Length - missing.Length, missing);
    }

    /// <summary>Keep a valid remembered selection or return the first valid discovered installation.</summary>
    public static string? RecoverSelection(IGameFiles files, string? remembered, IEnumerable<string> discovered)
    {
        if (!string.IsNullOrEmpty(remembered) && Survey(files, remembered).IsComplete)
            return remembered;
        return discovered.FirstOrDefault(root => Survey(files, root).IsComplete);
    }
}
