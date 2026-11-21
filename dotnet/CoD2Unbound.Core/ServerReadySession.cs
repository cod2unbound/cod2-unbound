using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public sealed record ServerReadyState(string? Address = null, ConfigReadyReport? Report = null, bool Checking = false, bool Evaluated = false);

/// <summary>The selector and temporary overrides are scoped to one canonical endpoint.</summary>
public sealed class ServerReadySession(ConfigReadyCheck checker, ServerQueryClient query, ConfigStudioSession studio)
{
    private readonly Lock gate = new();
    private readonly ConfigServerConnection connection = new();
    private int revision;
    private GameServer? observed;
    public ServerReadyState State { get; private set; } = new();
    public string? ProfileId => connection.ProfileId;

    public ServerReadyState Evaluate(string address, GameServer? server, ConfigCatalogMode mode)
    {
        lock (gate)
        {
            string canonical = LaunchLink.CanonicalAddress(address);
            connection.Select(canonical);
            revision++;
            observed = SameEndpoint(server, canonical) ? server : null;
            return State = new(canonical, Report(canonical, mode), Evaluated: true);
        }
    }

    public async Task<ServerReadyState> RefreshAsync(string address, ConfigCatalogMode mode, CancellationToken cancellationToken = default)
    {
        string canonical = LaunchLink.CanonicalAddress(address);
        int request;
        lock (gate)
        {
            connection.Select(canonical);
            request = ++revision;
            bool same = State.Address == canonical;
            if (!same)
                observed = null;
            State = new(canonical, same ? State.Report : null, Checking: true);
        }

        GameServer? server = null;
        bool finished = false;
        try
        {
            server = await query.ServerAsync(canonical, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            finished = true;
        }
        finally
        {
            lock (gate)
            {
                if (request == revision)
                {
                    State = State with { Checking = false };
                    if (finished)
                    {
                        observed = SameEndpoint(server, canonical) ? server : null;
                        State = new(canonical, Report(canonical, mode), Evaluated: true);
                    }
                }
            }
        }

        lock (gate)
            return State;
    }

    public string? UseLoadout(string? id, ConfigCatalogMode mode)
    {
        lock (gate)
        {
            string address = State.Address ?? throw new InvalidOperationException("Choose a server before choosing a loadout.");
            bool alternate = id is not null && !id.Equals(studio.ReadActive()?.Profile.Id, StringComparison.OrdinalIgnoreCase);
            if (alternate)
            {
                var profile = studio.ReadProfile(id!);
                connection.UseProfile(profile.Profile.Id);
                State = new(address, Report(address, mode), Evaluated: true);
                return $"Using {profile.Profile.DisplayName} for this game only.";
            }
            connection.Clear();
            State = new(address, Report(address, mode), Evaluated: true);
            return null;
        }
    }

    public string Fix()
    {
        lock (gate)
        {
            if (State.Report is not { } report || State.Checking)
                throw new InvalidOperationException("There is no verified temporary fix for this server and loadout.");
            connection.Fix(report);
            return "Fixed for this server only. Your loadout is unchanged.";
        }
    }

    public void Undo(ConfigCatalogMode mode) => UseLoadout(null, mode);

    public (string? ProfileId, ConfigConnectionLaunch? Connection, ConfigProfileVersion? Expected, ImmutableArray<byte>? Chain)
        Prepare(LaunchLink link, GameServer? current, ConfigCatalogMode mode)
    {
        lock (gate)
        {
            string canonical = LaunchLink.CanonicalAddress(link.Address);
            if (connection.Address != canonical)
                return default;
            var profile = Profile();
            if (profile is null || connection.Overrides.IsEmpty)
                return (connection.ProfileId, null, null, null);
            var server = SameEndpoint(current, canonical) ? current : observed;
            var report = checker.Evaluate(server, profile, mode, requireFresh: false);
            return (connection.ProfileId, connection.Launch(link, profile, report), profile.Version, report.AdvancedChain);
        }
    }

    private ConfigProfileRead? Profile() => connection.ProfileId is { } id ? studio.ReadProfile(id) : studio.ReadActive();

    private ConfigReadyReport? Report(string address, ConfigCatalogMode mode)
    {
        var profile = Profile();
        return profile is null ? null : checker.Evaluate(SameEndpoint(observed, address) ? observed : null, profile, mode);
    }

    private static bool SameEndpoint(GameServer? server, string address) => server is not null &&
        ServerBrowser.TryAddress(server.Address) == address;
}
