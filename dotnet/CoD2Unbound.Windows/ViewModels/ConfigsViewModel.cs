using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed partial class ConfigsViewModel : IConfigImportStore
{
    private readonly WindowsConfigProfiles files;
    private readonly WindowsLauncherSession session;
    private readonly ConfigDirectory directory;
    private ConfigDirectorySnapshot? cachedCatalogue;

    internal ConfigsViewModel(WindowsLauncherSession session, nint owner)
    {
        this.session = session;
        files = new(session.HomePath, owner);
        Studio = new(files, session.Settings.Working.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock,
            () => session.ClientIsRunning, TimeProvider.System);
        Studio.Refresh();
        Catalog = ConfigCatalog.LoadBundled();
        Model = new(Catalog, Dialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock, CultureInfo.CurrentCulture);
        directory = WindowsConfigFetch.Create(session.HomePath, session.TestMaster is not null);
        Discover = new(directory, Catalog);
    }

    internal async Task<ImmutableArray<ConfigDirectorySavedEntry>> ReadSavedSetupsAsync()
    {
        var snapshot = ShareCatalogue ?? await directory.ReadCachedSnapshotAsync();
        if (snapshot is null)
            return [];
        cachedCatalogue = snapshot;
        return await directory.SavedAsync(snapshot);
    }

    internal async Task<IReadOnlyDictionary<string, string>> ReadSavedSettingsAsync(string id)
    {
        var snapshot = ShareCatalogue ?? throw new InvalidOperationException("The player directory isn’t available.");
        var catalog = new ConfigImportCatalogAdapter(Catalog);
        var bundle = await directory.LoadProfileAsync(id, snapshot, Dialect, catalog);
        return ConfigExpertEdits.ReviewedSettings(bundle, catalog);
    }

    internal async Task LoadSharingAsync()
    {
        await Share.LoadAsync();
        if (!State.Profiles.Any(profile => Share.CanStop(profile.Id)))
            return;
        try { cachedCatalogue = await directory.ReadCachedSnapshotAsync(); }
        catch (Exception error) when (error is ConfigDirectoryException or IOException or UnauthorizedAccessException) { }
    }

    internal ConfigShareSession Share => session.Share;
    internal ConfigDirectorySnapshot? ShareCatalogue => Discover.State.Snapshot ?? cachedCatalogue;
    internal void RememberVerifiedCatalogue() => cachedCatalogue = ShareCatalogue;
    internal string AppVersion => session.Version;

    internal Task<ConfigShareForm> PrepareShareAsync(ConfigProfile profile) => Task.Run(() =>
        Share.PrepareAsync(profile, () => Studio.ReadProfile(profile.Id), new ConfigImportCatalogAdapter(Catalog)));

    internal void Record(UsageEvent? item) => session.Record(item);
    internal void Opened(UsagePage page) => session.Opened(page);
    internal void Error(UsageProblem kind) => session.ShowError(kind);
    internal void CatalogueEvent(UsageKind kind, string id)
    {
        if (Discover.State.Snapshot is { } snapshot)
            Record(UsageEvent.Catalogue(kind, snapshot, id));
    }

    internal ConfigStudioSession Studio { get; }
    internal ConfigCatalog Catalog { get; }
    internal ConfigStudioModel Model { get; }
    internal ConfigDiscover Discover { get; }
    internal double MouseDpi => DpiFor(State.Current);
    internal double DpiFor(ConfigProfileRead? loadout) => ConfigMouseDpi.For(loadout);
    internal Task<MapPixels?> ReadMapArtAsync(string map) => Task.Run(() => session.ReadMapArtwork(map));
    internal ConfigStudioState State => Studio.State;
    internal Task<(WindowsProfileDiscovery Found, string[] Searched)> FindInstalledProfilesAsync(
        string? folder, CancellationToken token) => session.FindInstalledProfilesAsync(folder, token);
    internal Task SelectLoadoutAsync(string id) => session.SelectLoadoutAsync(id);
    internal string ConfigPath(string id) => WindowsFiles.Under(session.HomePath, $"main/players/{id}/config_mp.cfg");

    internal ValueTask WriteExportAsync(string path, ImmutableArray<byte> bytes)
    {
        string relative = Path.GetRelativePath(session.HomePath, path);
        if (!Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Choose an export location outside the game's writable home.");
        var target = new WindowsSettingsFolder(Path.GetDirectoryName(path)!);
        return target.WriteAsync(Path.GetFileName(path), bytes, default);
    }

    internal event Action? SettingSaveChanged;
    internal (string Title, string Reason, string Summary)? SettingSaveProblem { get; private set; }
    internal GameSettings DisplaySettings => session.Settings.Working;
    public ConfigDialect Dialect => Studio.Dialect;

    internal string NewId(string name) => LoadoutName.Identifier(name, State.Profiles.Select(profile => profile.Id));
    internal string? NameProblem(string name, string? except = null) => LoadoutName.Problem(name,
        State.Profiles.Where(profile => profile.Id != except).Select(profile => profile.DisplayName), Studio.IsReadOnly);

    internal void Duplicate(string id)
    {
        var source = Studio.ReadProfile(id);
        Create(LoadoutName.CopyOf(source.Profile.DisplayName, State.Profiles.Select(profile => profile.DisplayName)), source);
    }

    internal void Create(string name, ConfigProfileRead? copyFrom = null)
    {
        try
        {
            var created = copyFrom is null ? Studio.Create(NewId(name), name) : Studio.Duplicate(copyFrom, NewId(name), name);
            Studio.Focus(created.Profile.Id);
            Record(new(UsageKind.LoadoutMade));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigProfileException)
        {
            Error(UsageProblem.SaveLoadout);
            throw;
        }
    }

    internal void Rename(ConfigProfileRead loadout, string name) => Studio.Mutate(loadout.Profile.Id,
        store => store.Update(loadout.Profile.Id, loadout.Document, loadout.Version,
            loadout.Metadata with { DisplayName = name }, "Renamed loadout"));

    internal ConfigProfileTrashHandle Delete(string id)
    {
        try { return Studio.Trash(id, files); }
        catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.CannotTrash)
        {
            throw new IOException("The loadout couldn’t be moved to the Recycle Bin. Nothing was removed.", error);
        }
    }

    internal void Restore(string id, ConfigProfileTrashHandle handle) => Studio.RestoreTrash(id, handle, files);

    internal void Set(ConfigProfileRead loadout, ConfigSettingSpec spec, string value)
    {
        if (SettingSaveProblem is not null)
            return;
        if (Model.TakeTheirsBlock(spec, State, null, [], "") is { } problem)
            throw new IOException(problem);
        var doc = loadout.Document.SetSetting(spec.Name, value);
        if (doc.Serialized.AsSpan().SequenceEqual(loadout.Version.Config.AsSpan()))
            return;
        Studio.SetDraft(doc);
        SaveSetting(spec.Name == "sensitivity" ? "Mouse speed" : spec.Title, $"Changed {spec.Title}");
    }

    internal void RetrySetting()
    {
        if (SettingSaveProblem is { } failure)
            SaveSetting(failure.Title, failure.Summary);
    }

    internal void DiscardSetting()
    {
        try
        {
            Studio.DiscardSettingDraft();
            SettingSaveProblem = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (SettingSaveProblem is { } problem)
                SettingSaveProblem = (problem.Title, error.Message, problem.Summary);
        }
        SettingSaveChanged?.Invoke();
    }

    private void SaveSetting(string title, string summary)
    {
        bool recovering = SettingSaveProblem is not null;
        try
        {
            Studio.SaveSettingDraft(summary);
            SettingSaveProblem = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error(UsageProblem.SaveLoadout);
            SettingSaveProblem = (title, error.Message, summary);
        }
        if (recovering || SettingSaveProblem is not null)
            SettingSaveChanged?.Invoke();
    }

    internal void UndoImport(ConfigImportReceipt receipt, bool created) => Studio.UndoImport(receipt, created, files);

    internal void Bind(ConfigBindingEdit edit, bool replace) =>
        Studio.Mutate(edit.ProfileId, store => ConfigBindings.Apply(edit, store, replace));

    internal void RestoreHistory(ConfigProfileRead profile, ConfigHistoryEntry entry, ConfigSnapshotRevision revision) =>
        Studio.Mutate(profile.Profile.Id, store => store.Restore(profile.Profile.Id, entry.Id, profile.Version, revision));

    public ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken) =>
        Studio.CommitReviewedImportAsync(write, cancellationToken);
}
