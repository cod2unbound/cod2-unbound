namespace CoD2Unbound.Core;

public static class UpdateNotification
{
    public static bool ShouldShow(string? automaticOffer, bool foreground, bool gameRunning, bool available,
        IEnumerable<string> notified) =>
        IsVersion(automaticOffer) && !foreground && !gameRunning && available &&
        !notified.Contains(automaticOffer, StringComparer.Ordinal);

    public static string Tag(string version) => $"update-{version}";

    public static bool IsVersion(string? version) =>
        version is not null && WindowsReleaseAssets.ParseVersion(version) is not null;
}
