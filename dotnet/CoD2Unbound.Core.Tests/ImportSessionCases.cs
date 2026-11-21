using System.Collections.Immutable;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static readonly FixtureImportCatalog SessionCatalog = new(new()
    {
        ["sensitivity"] = new(Archived: true), ["name"] = new(Archived: true),
        ["r_mode"] = new(Archived: true), ["r_fullscreen"] = new(Archived: true)
    }, ["+attack", "+forward"]);

    private static void ImportSessionCases()
    {
        using var fixture = Fixtures("import-session");
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = row.String("id")!;
            Check($"import session {id}", () =>
            {
                var source = row.String("platform") == "windows" ? ConfigImportPlatform.Windows : ConfigImportPlatform.Native;
                var session = NewSession(row.String("input")!, source);
                if (row.String("prior") is { } prior)
                    session.SetTarget(new ConfigImportTarget.Existing(ProfileRead(prior, [.. row.Strings("locks").Select(value => value!)])));
                if (row.GetProperty("everything").GetBoolean())
                    session.SetMode(ConfigImportMode.Everything);
                if (id == "omitted-helper")
                    session.Select(session.Rows[0].Id, false);
                Sequence(row.Strings("selected"), session.SelectedIds.Order(StringComparer.Ordinal));
                var rows = session.Rows;
                var expected = row.GetProperty("rows");
                Equal(expected.GetArrayLength(), rows.Length);
                for (int i = 0; i < rows.Length; i++)
                {
                    Equal(expected[i].String("id"), rows[i].Id);
                    Equal(expected[i].GetProperty("eligible").GetBoolean(), rows[i].CanSelect);
                    Equal(expected[i].String("old"), rows[i].OldValue);
                    Equal(expected[i].String("new"), rows[i].NewValue);
                    Equal(expected[i].String("platformNote"), rows[i].PlatformNote);
                    Sequence(expected[i].Strings("locks"), rows[i].LockedNames);
                }

                if (row.String("preview") == "accepted")
                {
                    var preview = session.Preview();
                    Sequence(row.Bytes("document"), preview.Document.Serialized);
                    Sequence(row.GetProperty("lastImportedValues").EnumerateObject()
                        .Select(value => (value.Name, value.Value.GetString())).Order(),
                        preview.Metadata.LastImportedValues.Select(value => (value.Key, (string?)value.Value)).Order());
                }
                else
                {
                    try { session.Preview(); }
                    catch (ConfigImportSessionException error) { Equal(row.String("preview"), error.Message); return; }
                    throw new InvalidDataException("An unsafe or empty selection passed preview.");
                }
            });
        }

        Check("reviewed import uses one compare-and-commit and leaves selection alone", () =>
        {
            var session = NewSession("seta sensitivity 4\nseta name Other\nseta password sample-secret\nexec absent.cfg\n");
            var target = (ConfigImportTarget.NewLoadout)session.Target;
            session.SetMode(ConfigImportMode.Everything);
            var store = new ImportStore();
            var receipt = Sync(session.ApplyAsync(store));
            Equal(target.Id, receipt.ProfileId);
            Equal(ConfigHistorySource.Import, receipt.HistoryEntry.Source);
            Equal(1, store.History.Count);
            Equal("Main", store.ActiveProfileId);
            var bytes = receipt.HistoryEntry.After.Config.AsSpan();
            Equal(false, bytes.IndexOf("password"u8) >= 0);
            Equal(false, bytes.IndexOf("exec"u8) >= 0);
            SessionRejects(ConfigImportSessionFailure.AlreadyApplied, () => Sync(session.ApplyAsync(store)));
            SessionRejects(ConfigImportSessionFailure.AlreadyApplied, () => session.ReplaceSelection([]));
            Equal(1, store.History.Count);
        });

        Check("import store refusal preserves state and permits retry", () =>
        {
            var session = NewSession("seta sensitivity 4\n");
            var original = ProfileRead("seta sensitivity 3\n", []);
            session.SetTarget(new ConfigImportTarget.Existing(original));
            var store = new ImportStore { Current = original };
            var selected = session.SelectedIds;
            ProfileRejects(ConfigProfileFailure.GameRunning, () =>
            {
                store.GameRunning = true;
                Sync(session.ApplyAsync(store));
            });
            store.GameRunning = false;
            store.HasDraft = true;
            ProfileRejects(ConfigProfileFailure.Conflict, () => Sync(session.ApplyAsync(store)));
            store.HasDraft = false;
            store.Current = ProfileRead("seta sensitivity 90\n", []);
            ProfileRejects(ConfigProfileFailure.Conflict, () => Sync(session.ApplyAsync(store)));
            store.Current = original;
            store.FailWrite = true;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => Sync(session.ApplyAsync(store)));
            store.FailWrite = false;
            Equal(original, store.Current);
            Equal(null, session.Receipt);
            Sequence(selected, session.SelectedIds);
            Equal(0, store.History.Count);
            SessionRejects(ConfigImportSessionFailure.InvalidBundle, () => Sync(session.ApplyAsync(new ImportStore { Dialect = ConfigDialect.Stock })));
            Sync(session.ApplyAsync(store));
            Equal(1, store.History.Count);
            Equal("4", store.Current!.Document.EffectiveSetting("sensitivity"));
        });

        Check("bulk replacement keeps eligible rows and refuses blocked and absent ids", () =>
        {
            string input = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"seta fixture_{i} {i}")) +
                "\nseta sensitivity 9\nseta r_mode 4\nseta password sample-secret\nbind MOUSE1 +forward\n";
            var session = NewSession(input);
            session.SetTarget(new ConfigImportTarget.Existing(ProfileRead("seta sensitivity 2\nbind MOUSE1 +attack\n",
                ["SENSITIVITY", "bind:mouse1"])));
            session.SetMode(ConfigImportMode.Everything);
            var expected = session.SelectedIds.Remove(session.SelectedIds.Order(StringComparer.Ordinal).First());
            session.ReplaceSelection(expected);
            Equal(ConfigImportMode.Review, session.Mode);
            Equal(true, expected.SetEquals(session.SelectedIds));
            foreach (var row in session.Rows.Where(row => !row.CanSelect))
                SessionRejects(ConfigImportSessionFailure.UnavailableSelection, () => session.ReplaceSelection(expected.Add(row.Id)));
            SessionRejects(ConfigImportSessionFailure.UnavailableSelection, () => session.ReplaceSelection(expected.Add("missing-row")));
            Equal(true, expected.SetEquals(session.SelectedIds));
        });

        Check("old setting credentials stay redacted", () =>
        {
            var session = NewSession("seta sensitivity 4\n");
            session.SetTarget(new ConfigImportTarget.Existing(ProfileRead(
                "seta sensitivity \"https://example.invalid/a?token=sample-secret\"\n", [])));
            Equal(null, session.Rows.Single().OldValue);
            Equal("4", session.Rows.Single().NewValue);
        });

        Check("overlapping applies cannot commit twice", () =>
        {
            var session = NewSession("seta sensitivity 4\n");
            var store = new GatedImportStore();
            var pending = session.ApplyAsync(store).AsTask();
            store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            SessionRejects(ConfigImportSessionFailure.ApplyInProgress, () => Sync(session.ApplyAsync(store)));
            SessionRejects(ConfigImportSessionFailure.ApplyInProgress, () => session.SetMode(ConfigImportMode.Everything));
            store.Resume.SetResult();
            Equal(pending.GetAwaiter().GetResult(), session.Receipt);
            Equal(1, store.Store.History.Count);
        });

        Check("Windows destination keeps device settings machine-owned for any source", () =>
        {
            var session = NewSession("seta sensitivity 6\nseta r_mode 4\nseta in_mouse 1\nbind COMMAND +attack\n",
                destination: ConfigImportPlatform.Windows);
            session.SetMode(ConfigImportMode.Everything);
            Equal(1, session.SelectedIds.Count);
            Equal(3, session.Rows.Count(row => row.PlatformNote is not null));
            Equal("6", session.Preview().Document.EffectiveSetting("sensitivity"));
        });
    }

    private static ConfigImportSession NewSession(string text, ConfigImportPlatform source = ConfigImportPlatform.Native,
        ConfigImportPlatform destination = ConfigImportPlatform.Native)
    {
        var file = new ConfigImportFile("config.cfg", new(ImportBytes(text).AsSpan(), ConfigDialect.Cod2x));
        return new(new(new(ConfigImportSourceKind.Paste), file.Path, [file], []), ConfigDialect.Cod2x, SessionCatalog,
            "Imported", sourcePlatform: source, destinationPlatform: destination);
    }

    private static ConfigProfileRead ProfileRead(string text, ImmutableArray<string> locks)
    {
        var bytes = ImportBytes(text);
        return new(new("Main", "Main", true), new(bytes.AsSpan(), ConfigDialect.Cod2x),
            new("Main") { Locks = locks.ToImmutableHashSet(StringComparer.Ordinal) }, new(bytes, null));
    }

    private static void SessionRejects(ConfigImportSessionFailure failure, Action operation)
    {
        try { operation(); }
        catch (ConfigImportSessionException error) { Equal(failure, error.Failure); return; }
        throw new InvalidDataException($"Expected session failure {failure}.");
    }

    private static void ProfileRejects(ConfigProfileFailure failure, Action operation)
    {
        try { operation(); }
        catch (ConfigProfileException error) { Equal(failure, error.Failure); return; }
        throw new InvalidDataException($"Expected profile failure {failure}.");
    }

    private sealed class GatedImportStore : IConfigImportStore
    {
        public ImportStore Store { get; } = new();
        public ConfigDialect Dialect => Store.Dialect;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await Store.CommitReviewedAsync(write, cancellationToken);
        }
    }

    private sealed class ImportStore : IConfigImportStore
    {
        public ConfigDialect Dialect { get; init; } = ConfigDialect.Cod2x;
        public ConfigProfileRead? Current { get; set; }
        public bool GameRunning { get; set; }
        public bool HasDraft { get; set; }
        public bool FailWrite { get; set; }
        public string ActiveProfileId => "Main";
        public List<ConfigImportReceipt> History { get; } = [];

        public ValueTask<ConfigImportReceipt> CommitReviewedAsync(ConfigImportWrite write, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GameRunning)
                throw new ConfigProfileException(ConfigProfileFailure.GameRunning);
            if (HasDraft || (write.Expected is { } expected && !expected.Matches(Current?.Version)))
                throw new ConfigProfileException(ConfigProfileFailure.Conflict);
            if (FailWrite)
                throw new ConfigProfileException(ConfigProfileFailure.CannotPublish);
            var before = Current?.Version ?? new([], null);
            var after = new ConfigProfileVersion(write.Document.Serialized, null);
            Current = new(new(write.ProfileId, write.Metadata.DisplayName, write.ProfileId == ActiveProfileId),
                write.Document, write.Metadata, after);
            var receipt = new ConfigImportReceipt(write.ProfileId,
                new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, ConfigHistorySource.Import, write.Summary, before, after));
            History.Add(receipt);
            return ValueTask.FromResult(receipt);
        }
    }
}
