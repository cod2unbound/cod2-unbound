using System.Globalization;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed class ConfigsViewModel : IConfigImportStore
{
    private readonly WindowsConfigProfiles files;
    private readonly WindowsLauncherSession session;

    internal ConfigsViewModel(WindowsLauncherSession session, nint owner)
    {
        this.session = session;
        files = new(session.HomePath, owner);
        Studio = new(files, session.Settings.Working.Engine == GameEdition.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock,
            () => session.ClientIsRunning, TimeProvider.System);
        Studio.Refresh();
        Catalog = ConfigCatalog.LoadBundled();
        Model = new(Catalog, Dialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock, CultureInfo.CurrentCulture);
        Discover = new(WindowsConfigFetch.Create(session.HomePath, session.TestMaster is not null), Catalog);
    }

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
    internal double DpiFor(ConfigProfileRead? loadout) => loadout?.Metadata.Dpi ?? session.Settings.Working.Dpi;
    internal Task<MapPixels?> ReadMapArtAsync(string map) => Task.Run(() => session.ReadMapArtwork(map));
    internal MatchSummary? LastMatch => session.Settings.Working.MatchSummariesEnabled ? session.ReadMatches().LastOrDefault() : null;
    internal ConfigStudioState State => Studio.State;
    public ConfigDialect Dialect => Studio.Dialect;

    internal string NewId(string name) => LoadoutName.Identifier(name, State.Profiles.Select(profile => profile.Id));
    internal string? NameProblem(string name, string? except = null) => LoadoutName.Problem(name,
        State.Profiles.Where(profile => profile.Id != except).Select(profile => profile.DisplayName), Studio.IsReadOnly);

    internal void Create(string name)
    {
        try
        {
            var created = Studio.Create(NewId(name), name);
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
        if (Model.TakeTheirsBlock(spec, State, null, [], "") is { } problem)
            throw new IOException(problem);
        var doc = loadout.Document.SetSetting(spec.Name, value);
        if (doc.Serialized.AsSpan().SequenceEqual(loadout.Version.Config.AsSpan()))
            return;
        Studio.Mutate(loadout.Profile.Id,
            store => store.Update(loadout.Profile.Id, doc, loadout.Version, null, $"Changed {spec.Title}"));
    }

    internal void Bind(ConfigBindingEdit edit, bool replace) =>
        Studio.Mutate(edit.ProfileId, store => ConfigBindings.Apply(edit, store, replace));

    internal void RestoreHistory(ConfigProfileRead profile, ConfigHistoryEntry entry) =>
        Studio.Mutate(profile.Profile.Id, store => store.Restore(profile.Profile.Id, entry.Id, profile.Version, ConfigSnapshotRevision.Before));

    public ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken) =>
        Studio.CommitReviewedImportAsync(write, cancellationToken);
}
