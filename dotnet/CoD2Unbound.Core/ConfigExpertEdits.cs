namespace CoD2Unbound.Core;

public static class ConfigExpertEdits
{
    public static IReadOnlyDictionary<string, string> ReviewedSettings(ConfigImportBundle bundle, IConfigImportCatalog catalog)
    {
        var review = new ConfigImportSafety(catalog).Review(bundle);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in review.Candidates)
        {
            var command = candidate.Command;
            if (candidate.Classification == ConfigImportClassification.Safe && candidate.SelectedByDefault &&
                command.Kind == ConfigCommandKind.Setting && command.Tokens.Length >= 3)
                settings[command.Tokens[1].Text] = System.Text.Encoding.Latin1.GetString(
                    ConfigDocument.Combine(command.Tokens, 2, binding: false).AsSpan());
        }
        return settings;
    }

    public static ConfigHistoryEntry EditMetadata(ConfigStudioSession studio, ConfigProfileRead profile,
        ConfigSettingSpec spec, bool? locked, string? note)
    {
        string name = spec.Name.ToLowerInvariant();
        var metadata = profile.Metadata;
        string summary;
        if (locked is { } value)
        {
            var locks = metadata.Locks.Except(metadata.Locks.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)));
            metadata = metadata with { Locks = value ? locks.Add(name) : locks };
            summary = value ? $"Locked {spec.Title}" : $"Unlocked {spec.Title}";
        }
        else
        {
            string text = note?.Trim() ?? "";
            var notes = metadata.Notes.RemoveRange(metadata.Notes.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)));
            metadata = metadata with { Notes = text.Length == 0 ? notes : notes.SetItem(name, text) };
            summary = text.Length == 0 ? $"Removed the note on {spec.Title}" : $"Noted {spec.Title}";
        }
        return studio.Mutate(profile.Profile.Id, store =>
            store.Update(profile.Profile.Id, profile.Document, profile.Version, metadata, summary));
    }

    public static ConfigHistoryEntry Reset(ConfigStudioModel model, ConfigStudioSession studio, ConfigProfileRead profile,
        ConfigSettingSpec spec)
    {
        if (model.TakeTheirsBlock(spec, studio.State, null, [], "") is { } reason)
            throw new IOException(reason);
        return studio.Mutate(profile.Profile.Id, store => store.Update(profile.Profile.Id,
            profile.Document.RemoveSetting(spec.Name), profile.Version, null, $"Reset {spec.Title} to default"));
    }

    public static ConfigHistoryEntry ApplyText(ConfigStudioSession studio, ConfigProfileRead source,
        string text, ConfigCatalog catalog, ConfigCatalogMode mode)
    {
        var review = ConfigTextCheck.Inspect(text, studio.Dialect, catalog, mode);
        if (!review.CanApply)
            throw new InvalidDataException("Fix the underlined problems to apply your edits.");
        return studio.Mutate(source.Profile.Id, store =>
            store.Update(source.Profile.Id, review.Document!, source.Version, null, "Edited as text"));
    }
}
