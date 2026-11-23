namespace CoD2Unbound.Core;

public static class ConfigMouseDpi
{
    public static double For(ConfigProfileRead? loadout) => loadout?.Metadata.Dpi ?? double.NaN;

    public static double? DistancePerSensitivity(ConfigProfileRead? loadout)
    {
        double yaw = loadout?.Document.EffectiveSetting("m_yaw") is { } saved
            ? Math.Abs(ConfigValues.Number(saved) ?? 0) : 0.022;
        return MouseDistance.Centimetres(For(loadout), 1, yaw);
    }

    public static ConfigHistoryEntry Save(ConfigStudioSession studio, ConfigProfileRead profile, double? dpi) =>
        studio.Mutate(profile.Profile.Id, store => store.Update(profile.Profile.Id, profile.Document, profile.Version,
            profile.Metadata with { Dpi = dpi }, dpi.HasValue ? "Changed mouse DPI" : "Cleared mouse DPI"));
}
