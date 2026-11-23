namespace CoD2Unbound.Core;

public static class UpdateNotification
{
    public static bool ShouldShow(string? automaticOffer, bool foreground, LauncherActivity activity, bool available,
        IEnumerable<string> notified) =>
        IsVersion(automaticOffer) && !foreground && !activity.GameRunning && !activity.ServersRunning && available &&
        !notified.Contains(automaticOffer, StringComparer.Ordinal);

    public static string Tag(string version) => $"update-{version}";

    public static bool IsVersion(string? version) =>
        version is not null && WindowsReleaseAssets.ParseVersion(version) is not null;
}
