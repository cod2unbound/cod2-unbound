using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void DirectoryCases()
    {
        SetupDiscoveryCases();
        using var fixture = Fixtures("directory");
        var root = fixture.RootElement;
        var index = new Uri(root.String("indexURL")!);
        var signature = new Uri(root.String("signatureURL")!);
        var trust = new ConfigDirectoryConfiguration(index, signature, ImmutableArray.Create(root.Bytes("publicKey")));
        var rows = root.GetProperty("cases").EnumerateArray().ToDictionary(row => row.String("id")!);
        var source = new DirectoryFetcher();
        void LoadRow(string name)
        {
            var row = rows[name];
            source.Responses.Clear();
            source.Responses[index] = new(ImmutableArray.Create(row.Bytes("index")), new Uri(row.String("finalURL")!), null);
            source.Responses[signature] = new(ImmutableArray.Create(row.Bytes("signature")), signature, null);
        }

        Check("pinned production directory key", () =>
            Equal(root.String("productionPublicKey"), Convert.ToBase64String(ConfigDirectory.Production.PublicKey.AsSpan())));
        foreach (var (name, row) in rows)
        {
            Check($"signed directory {name}", () =>
            {
                LoadRow(name);
                var directory = new ConfigDirectory(trust, new DirectoryStorage(), source);
                string? outcome;
                try { outcome = Load(directory).Snapshot is null ? "unavailable" : "verified"; }
                catch (ConfigDirectoryException error) { outcome = error.Message; }
                Equal(row.String("outcome"), outcome);
            });
        }

        Check("directory cache, bookmarks, signed profile and rollback", () =>
        {
            LoadRow("signed");
            var storage = new DirectoryStorage();
            var directory = new ConfigDirectory(trust, storage, source);
            Equal(null, Sync(directory.CacheDateAsync()));
            var snapshot = Load(directory).Snapshot!;
            Equal(DateTimeOffset.UnixEpoch, Sync(directory.CacheDateAsync()));
            var entry = snapshot.Index.Entries.Single();
            Equal(ConfigDirectoryAttribution.SelfPublished, entry.Provenance.Attribution);
            Equal(ConfigDirectoryPlatform.Native, entry.Platform);
            Sync(directory.SetSavedAsync(entry.Id, true, snapshot));
            Equal(entry.Id, Sync(directory.SavedAsync(snapshot)).Single().Entry!.Id);
            source.Responses[entry.ConfigUrl] = new(ImmutableArray.Create(root.Bytes("profile")), entry.ConfigUrl, "text/plain");
            Sequence(root.Bytes("profile"), Sync(directory.LoadProfileAsync(entry.Id, snapshot, ConfigDialect.Stock, ImportCatalog)).Files.Single().Document.Serialized);
            source.Responses[entry.ConfigUrl] = new(ImmutableArray.Create(root.Bytes("profile")), new Uri(index, "other.cfg"), null);
            DirectoryRejects(ConfigDirectoryFailure.IdentityMismatch, () => Sync(directory.LoadProfileAsync(entry.Id, snapshot, ConfigDialect.Stock, ImportCatalog)));
            var altered = root.Bytes("profile");
            altered[0] ^= 1;
            source.Responses[entry.ConfigUrl] = new(ImmutableArray.Create(altered), entry.ConfigUrl, null);
            DirectoryRejects(ConfigDirectoryFailure.ProfileMismatch, () => Sync(directory.LoadProfileAsync(entry.Id, snapshot, ConfigDialect.Stock, ImportCatalog)));
            source.Responses.Clear();
            var offline = Load(directory);
            Equal(ConfigDirectoryOrigin.Cache, offline.Snapshot!.Origin);
            Equal(true, offline.RefreshError is not null);
            Equal(entry.Id, Sync(directory.SavedAsync(offline.Snapshot)).Single().Entry!.Id);
            LoadRow("signed");
            Equal(ConfigDirectoryOrigin.Network, Load(directory).Snapshot!.Origin);
            LoadRow("same-revision-edited");
            DirectoryRejects(ConfigDirectoryFailure.Rollback, () => Load(directory));
            LoadRow("newer");
            var newer = Load(directory).Snapshot!;
            Equal(2L, newer.Index.Revision);
            Equal(null, Sync(directory.SavedAsync(newer)).Single().Entry);
            Sync(directory.SetSavedAsync(entry.Id, false, newer));
            Equal(0, Sync(directory.SavedAsync(newer)).Length);
            LoadRow("signed");
            DirectoryRejects(ConfigDirectoryFailure.Rollback, () => Load(directory));
            storage.Files[ConfigDirectoryFile.Cache] = ImmutableArray.Create(Encoding.UTF8.GetBytes("tampered cache"));
            source.Responses.Clear();
            Equal(null, Load(directory).Snapshot);
            Equal(false, storage.Files.ContainsKey(ConfigDirectoryFile.Cache));
            LoadRow("signed");
            Equal(ConfigDirectoryOrigin.Network, Load(directory).Snapshot!.Origin);
        });

        Check("directory fallback distinguishes rejected content, HTTP errors and absent trust", () =>
        {
            LoadRow("signed");
            var storage = new DirectoryStorage();
            var directory = new ConfigDirectory(trust, storage, source);
            var missingTrust = Load(new ConfigDirectory(null, storage, source));
            Equal("Player setups aren’t available in this build.", missingTrust.UnavailableReason);
            Load(directory);
            foreach (int status in new[] { 404, 429, 503 })
            {
                source.HttpStatus = status;
                Equal(ConfigDirectoryOrigin.Cache, Load(directory).Snapshot!.Origin);
            }
            source.HttpStatus = 429;
            try
            {
                Load(new ConfigDirectory(trust, new DirectoryStorage(), source));
                throw new InvalidDataException("An HTTP refusal without a cache succeeded.");
            }
            catch (ConfigFetchHttpException error) { Equal(429, error.StatusCode); }
            source.HttpStatus = null;
            LoadRow("tampered");
            DirectoryRejects(ConfigDirectoryFailure.InvalidSignature, () => Load(directory));
        });

        Check("directory cache rejects a modified signature and a different trust root", () =>
        {
            var storage = new DirectoryStorage();
            var directory = new ConfigDirectory(trust, storage, source);
            LoadRow("signed");
            var snapshot = Load(directory).Snapshot!;
            var original = storage.Files[ConfigDirectoryFile.Cache];
            var changed = JsonNode.Parse(original.AsSpan())!.AsObject();
            changed["signature"] = Convert.ToBase64String(new byte[64]);
            storage.Files[ConfigDirectoryFile.Cache] = ImmutableArray.Create(Encoding.UTF8.GetBytes(changed.ToJsonString()));
            source.Responses.Clear();
            Equal(null, Load(directory).Snapshot);
            Equal(false, storage.Files.ContainsKey(ConfigDirectoryFile.Cache));

            storage.Files[ConfigDirectoryFile.Cache] = original;
            var otherTrust = new ConfigDirectoryConfiguration(new Uri(index, "other.json"), signature, trust.PublicKey);
            var otherDirectory = new ConfigDirectory(otherTrust, storage, source);
            Equal(null, Load(otherDirectory).Snapshot);
            DirectoryRejects(ConfigDirectoryFailure.InvalidConfiguration, () => Sync(otherDirectory.SavedAsync(snapshot)));
        });

        Check("unsafe cache paths fail closed and are left untouched", () =>
        {
            LoadRow("signed");
            var storage = new DirectoryStorage { Unsafe = true };
            var directory = new ConfigDirectory(trust, storage, source);
            DirectoryRejects(ConfigDirectoryFailure.UnsafeCache, () => Load(directory));
            Equal(0, storage.Files.Count);
        });

        Check("late signed response cannot overwrite newer concurrent publication", () =>
        {
            LoadRow("signed");
            var old = new GatedDirectoryFetcher(new(source.Responses));
            var storage = new DirectoryStorage();
            var older = new ConfigDirectory(trust, storage, old).LoadAsync().AsTask();
            old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            LoadRow("newer");
            Equal(2L, Load(new ConfigDirectory(trust, storage, source)).Snapshot!.Index.Revision);
            old.Resume.SetResult();
            DirectoryRejects(ConfigDirectoryFailure.Rollback, () => older.GetAwaiter().GetResult());
        });
    }

    private static ConfigDirectoryLoad Load(ConfigDirectory directory) => Sync(directory.LoadAsync());
    private static T Sync<T>(ValueTask<T> value) => value.AsTask().GetAwaiter().GetResult();
    private static void Sync(ValueTask value) => value.AsTask().GetAwaiter().GetResult();

    private static void DirectoryRejects(ConfigDirectoryFailure failure, Action operation)
    {
        try { operation(); }
        catch (ConfigDirectoryException error) { Equal(failure, error.Failure); return; }
        throw new InvalidDataException($"Expected directory failure {failure}.");
    }

    private sealed class DirectoryFetcher : IConfigTextFetcher
    {
        public Dictionary<Uri, ConfigTextResponse> Responses { get; } = [];
        public int? HttpStatus { get; set; }

        public ValueTask<ConfigTextResponse> FetchAsync(ConfigFetchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HttpStatus is int status)
                throw new ConfigFetchHttpException(status);
            if (!Responses.TryGetValue(request.Url, out var response))
                throw new ConfigFetchUnavailableException("Offline fixture.");
            return ValueTask.FromResult(response);
        }
    }

    private sealed class GatedDirectoryFetcher(Dictionary<Uri, ConfigTextResponse> responses) : IConfigTextFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ConfigTextResponse> FetchAsync(ConfigFetchRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return responses[request.Url];
        }
    }

    private sealed class DirectoryStorage : IConfigDirectoryStorage
    {
        private readonly SemaphoreSlim gate = new(1);
        public Dictionary<ConfigDirectoryFile, ImmutableArray<byte>> Files { get; } = [];
        public bool Unsafe { get; init; }

        public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            if (Unsafe)
                throw new ConfigDirectoryException(ConfigDirectoryFailure.UnsafeCache);
            await gate.WaitAsync(cancellationToken);
            return new Lease(gate);
        }

        public ValueTask<ImmutableArray<byte>?> ReadAsync(ConfigDirectoryFile file, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ImmutableArray<byte>?>(Files.TryGetValue(file, out var value) && value.Length > limit
                ? value[..(limit + 1)] : Files.TryGetValue(file, out value) ? value : null);
        }

        public ValueTask<DateTimeOffset?> ModifiedAsync(ConfigDirectoryFile file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DateTimeOffset?>(Files.ContainsKey(file) ? DateTimeOffset.UnixEpoch : null);
        }

        public ValueTask WriteAsync(ConfigDirectoryFile file, ImmutableArray<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Files[file] = data;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(ConfigDirectoryFile file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Files.Remove(file);
            return ValueTask.CompletedTask;
        }

        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
