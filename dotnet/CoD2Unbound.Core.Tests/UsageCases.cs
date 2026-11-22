using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static readonly UsageFacts WindowsFacts = new("0.5.0", "10.0.26100", "Lenovo ThinkPad T14",
        "AMD Ryzen 7 7800X3D", 2560, 1440, 165, "en");
    private static readonly UsageEndpoint LoopbackUsage = UsageEndpoint.Create("phc_fixture", "http://127.0.0.1:9")!;

    private static void UsageCases()
    {
        Usage("nothing before an answer, only setup steps on disk", async () =>
        {
            var f = new UsageFixture();
            var usage = await f.Start();
            await usage.OpenedAsync();
            await usage.RecordAsync(UsageEvent.Page(UsagePage.Home));
            using var sample = JsonDocument.Parse(usage.Preview()!);
            Equal(Guid.Empty, sample.RootElement.GetProperty("distinct_id").GetGuid());
            Equal<Guid?>(null, usage.InstallNumber);
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.JournalFile, default));
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.SetupFile, default));
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Data, ownFiles: true));
            string setup = Encoding.UTF8.GetString((await f.Disk.ReadAsync(UsageData.SetupFile, default))!.Value.AsSpan());
            Equal(false, setup.Contains("Install", StringComparison.Ordinal));
            Equal(false, setup.Contains("First", StringComparison.Ordinal));
            Equal(0, f.Captures.Count);
        });
        Usage("restarted setup journal, consent ordering and held delivery", async () =>
        {
            var f = new UsageFixture();
            var before = await f.Start();
            await before.RecordAsync(UsageEvent.Setup(SetupStep.Data, ownFiles: true));
            f.Clock.Now += TimeSpan.FromMinutes(3);
            var usage = await f.Start();
            await usage.OpenedAsync();
            Equal(true, await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Setup, f.Persist));
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Ready));
            await Task.Delay(40);
            Equal(0, f.Captures.Count);
            Equal(4, usage.QueuedCount);
            var install = usage.InstallNumber;
            await usage.HoldAsync(false);
            await Until(() => usage.QueuedCount == 0);
            var received = Events(f.Captures);
            Sequence(new[] { "usage data turned on", "setup step reached", "app opened", "setup step reached" },
                received.Select(Name));
            Equal("2027-01-15T08:00:00Z", received[1].GetProperty("timestamp").GetString());
            Equal("own files", Props(received[1]).GetProperty("source").GetString());
            Equal(true, received.All(item => item.GetProperty("distinct_id").GetGuid() == install));
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.SetupFile, default));
            await usage.HoldAsync(true);
        });
        Usage("no clears the pre-question journal and stays off", async () =>
        {
            var f = new UsageFixture();
            var usage = await f.Start();
            await usage.OpenedAsync();
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Welcome));
            Equal(true, await usage.SetConsentAsync(UsageConsent.No, UsageSource.Setup, f.Persist));
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Ready));
            await usage.HoldAsync(false);
            Equal(UsageConsent.No, (await f.Start()).Consent);
            Equal<Guid?>(null, usage.InstallNumber);
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.SetupFile, default));
            Equal(0, f.Captures.Count);
        });
        Usage("failed settings write and missing install stay inactive", async () =>
        {
            var f = new UsageFixture();
            var usage = await f.Start();
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Welcome));
            await usage.OpenedAsync();
            bool threw = false;
            try { await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Setup, _ => throw new IOException("fixture")); }
            catch (IOException) { threw = true; }
            Equal(true, threw);
            Equal(true, usage.ShouldAsk);
            Equal<Guid?>(null, usage.InstallNumber);
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.JournalFile, default));
            Equal(true, (await f.Disk.ReadAsync(UsageData.SetupFile, default)).HasValue);
            f.Saved = UsageConsent.Yes;
            Equal(UsageConsent.No, (await f.Start()).Consent);
            Equal(0, f.Captures.Count);
        });
        Usage("build without key cannot create a journal or offer consent", async () =>
        {
            var f = new UsageFixture();
            var usage = await f.Start(available: false);
            await usage.OpenedAsync();
            await usage.RecordAsync(UsageEvent.Setup(SetupStep.Welcome));
            Equal(false, usage.Available);
            Equal(false, usage.ShouldAsk);
            Equal(false, await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, _ => throw new IOException()));
            Equal<string?>(null, usage.Preview());
            Equal<Guid?>(null, usage.InstallNumber);
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.SetupFile, default));
            Equal(0, f.Captures.Count);
        });
        Usage("app opened once per UTC day across restarts and resets", async () =>
        {
            var f = new UsageFixture();
            var first = await f.Start(setup: false);
            await first.OpenedAsync();
            await first.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, f.Persist);
            await Until(() => first.QueuedCount == 0);
            await first.HoldAsync(true);
            var same = await f.Start(setup: false);
            await same.OpenedAsync();
            await same.OpenedAsync();
            Equal(0, same.QueuedCount);
            Equal(true, await same.ResetAsync());
            await same.OpenedAsync();
            Equal(0, same.QueuedCount);
            f.Clock.Now += TimeSpan.FromDays(1);
            await same.OpenedAsync();
            await same.OpenedAsync();
            await Until(() => same.QueuedCount == 0);
            Equal(2, Events(f.Captures).Count(item => Name(item) == "app opened"));
            await same.HoldAsync(true);
        });
        Usage("failed send retries the same bytes when new events arrive", async () =>
        {
            var f = new UsageFixture();
            var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            f.Send = async token =>
            {
                if (Interlocked.Increment(ref count) != 1)
                    return true;
                firstRequest.SetResult();
                await release.Task.WaitAsync(token);
                return false;
            };
            var usage = await f.Start(setup: false);
            await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, f.Persist);
            await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await usage.RecordAsync(UsageEvent.Page(UsagePage.Home));
            release.SetResult();
            await Until(() => usage.QueuedCount == 0);
            var attempts = f.Captures.ToArray();
            Equal(3, attempts.Length);
            Sequence(attempts[0], attempts[1]);
            await usage.HoldAsync(true);
        });
        Usage("reset and off cancel an active send and re-enable generates a new number", async () =>
        {
            var f = new UsageFixture();
            int cancelled = 0;
            f.Send = async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return false; }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            };
            var usage = await f.Start(setup: false);
            await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, f.Persist);
            await Until(() => f.Captures.Count == 1);
            var old = usage.InstallNumber;
            Equal(true, await usage.ResetAsync());
            var fresh = usage.InstallNumber;
            Equal(false, old == fresh);
            Equal(0, usage.QueuedCount);
            Equal(fresh, (await f.Start()).InstallNumber);
            await Until(() => Volatile.Read(ref cancelled) == 1);
            await usage.RecordAsync(new(UsageKind.LoadoutMade));
            await Until(() => f.Captures.Count == 2);
            Equal(true, await usage.SetConsentAsync(UsageConsent.No, UsageSource.Settings, f.Persist));
            Equal<Guid?>(null, usage.InstallNumber);
            Equal(0, usage.QueuedCount);
            Equal<ImmutableArray<byte>?>(null, await f.Disk.ReadAsync(UsageData.JournalFile, default));
            await Until(() => Volatile.Read(ref cancelled) == 2);
            f.Send = _ => ValueTask.FromResult(true);
            var off = await f.Start(setup: false);
            Equal(UsageConsent.No, off.Consent);
            await off.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, f.Persist);
            Equal(false, off.InstallNumber == old || off.InstallNumber == fresh);
            await Until(() => off.QueuedCount == 0);
            await off.HoldAsync(true);
        });
        Usage("setup and send caps, expiry, FIFO batching", async () =>
        {
            var f = new UsageFixture();
            var usage = await f.Start();
            for (int i = 0; i < UsageData.SetupLimit + 3; i++)
                await usage.RecordAsync(UsageEvent.Setup(SetupStep.Welcome));
            var bytes = (await f.Disk.ReadAsync(UsageData.SetupFile, default))!.Value;
            using var setup = JsonDocument.Parse(bytes.AsMemory());
            Equal(UsageData.SetupLimit, setup.RootElement.GetProperty("Items").GetArrayLength());
            await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Setup, f.Persist);
            f.Clock.Now += TimeSpan.FromDays(8);
            for (int i = 0; i < UsageData.QueueLimit + 8; i++)
                await usage.RecordAsync(UsageEvent.End(false, i));
            Equal(UsageData.QueueLimit, usage.QueuedCount);
            await usage.HoldAsync(false);
            await Until(() => usage.QueuedCount == 0);
            var received = Events(f.Captures);
            Equal(UsageData.QueueLimit, received.Length);
            Equal(true, received.All(item => Name(item) == "game ended"));
            Sequence(Enumerable.Range(8, UsageData.QueueLimit), received.Select(item => Props(item).GetProperty("minutes").GetInt32()));
            Equal(10, f.Captures.Count);
            await usage.HoldAsync(true);
        });
        Usage("all Windows event kinds, hostile inputs, shared facts and exact preview", async () =>
        {
            string[] hostile = ["PRIVATE-HANDLE", "000000000000000086D3", "203.0.113.97:28960",
                "C:\\Users\\PRIVATE-HANDLE\\game", "seta name \"SECRET\"", "private-slug", "S-1-5-21-8871",
                "AA-BB-CC-DD-EE-FF", "95fcdc44-36ef-49f8-9726-bce60a27bc47", "PHYSIC-SERIAL123"];
            var f = new UsageFixture();
            var usage = await f.Start();
            await usage.OpenedAsync();
            await usage.SetConsentAsync(UsageConsent.Yes, UsageSource.Settings, f.Persist);
            var signed = SignedUsageCatalogue();
            string setupId = signed.Index.Entries.Single().Id;
            foreach (string value in hostile)
            {
                Equal<UsageEvent?>(null, UsageEvent.Catalogue(UsageKind.SetupViewed, signed, value));
                f.Facts = new(value, value, value, value, int.MaxValue, -1, int.MaxValue, value);
                foreach (var kind in Enum.GetValues<UsageKind>())
                    await usage.RecordAsync(new(kind, int.MaxValue, int.MinValue, -1, value, value));
                var settings = new GameSettings { Advanced = value, Resolution = value, Fullscreen = value, DismissedWarnings = [value] };
                foreach (var item in UsageEvent.Changed(new GameSettings { DismissedWarnings = ["a warning"] }, settings))
                    await usage.RecordAsync(item);
            }
            f.Facts = WindowsFacts;
            foreach (var eventKind in Enum.GetValues<UsageKind>().Except([UsageKind.AppOpened, UsageKind.TurnedOn, UsageKind.ServerJoined]))
            {
                var item = eventKind switch
                {
                    UsageKind.SetupViewed or UsageKind.SetupTried or UsageKind.SetupImported => UsageEvent.Catalogue(eventKind, signed, setupId),
                    UsageKind.UpdateOffered or UsageKind.UpdateInstalled or UsageKind.UpdateDownloading or UsageKind.UpdateVerifying or
                        UsageKind.UpdateReady or UsageKind.UpdateInstalling => UsageEvent.Update(eventKind, "0.4.2", "0.5.0"),
                    _ => new UsageEvent(eventKind)
                };
                await usage.RecordAsync(item);
            }
            f.ForcedId = Guid.Parse("e2f92677-15c0-4f83-b336-1f0998f4b27b");
            string preview = usage.Preview()!;
            f.ForcedId = Guid.Parse("e2f92677-15c0-4f83-b336-1f0998f4b27b");
            await usage.RecordAsync(UsageEvent.Join(UsageJoin.List, GameEdition.Cod2x));
            await usage.HoldAsync(false);
            await Until(() => usage.QueuedCount == 0);
            var received = Events(f.Captures);
            Equal(Enum.GetValues<UsageKind>().Length, received.Select(Name).Distinct().Count());
            string wire = string.Join("\n", f.Captures.Select(bytes => Encoding.UTF8.GetString(bytes)));
            Equal(false, hostile.Any(value => wire.Contains(value, StringComparison.Ordinal)));
            Equal(false, wire.Contains("$geoip_disable", StringComparison.Ordinal));
            Equal(false, wire.Contains("\"$ip\"", StringComparison.Ordinal));
            Equal(preview, received.Single(item => Name(item) == "server joined").GetRawText());
            foreach (var item in received)
            {
                var facts = Props(item);
                Equal(false, facts.GetProperty("$process_person_profile").GetBoolean());
                Equal("cod2unbound-launcher", facts.GetProperty("$lib").GetString());
                Equal(true, new[] { "app", "windows", "pc_model", "processor", "display_width", "display_height", "display_hz", "language" }
                    .All(name => facts.TryGetProperty(name, out _)));
                Equal(true, item.GetProperty("timestamp").GetString()!.EndsWith(":00Z", StringComparison.Ordinal));
            }
            await usage.HoldAsync(true);
        });
        Check("usage endpoint, firmware placeholders and restricted facts", () =>
        {
            foreach (var model in new[] { "", "System Product Name", "To Be Filled By O.E.M.", "Default string", "System Version", "Not Specified" })
                Equal("no model given", UsageFacts.Model("ASUS", model));
            Equal("Lenovo ThinkPad T14", UsageFacts.Model("Lenovo", "ThinkPad T14"));
            Equal("unknown", UsageFacts.Model("Dell", "PrivateHandle 666", "PrivateHandle"));
            Equal("unknown", UsageFacts.Model("Dell", "AABBCCDDEEFF"));
            Equal("AMD Ryzen 7 7800X3D", UsageFacts.SafeProcessor("  AMD Ryzen 7 7800X3D  "));
            Equal("unknown", UsageFacts.SafeProcessor("Intel(R) Core PrivateHandle 5", "PrivateHandle"));
            Equal("unknown", UsageFacts.SafeWindows("10.0.26100 Pro"));
            Equal(new Uri("http://127.0.0.1:9/batch/"), LoopbackUsage.Address);
            foreach (var address in new[] { "https://example.invalid", "http://127.0.0.1:9/?key=1", "http://u@127.0.0.1:9", "http://localhost:9" })
                Equal<UsageEndpoint?>(null, UsageEndpoint.Create("phc_fixture", address));
        });
        Check("legacy usage preference and both explicit answers survive reset", () =>
        {
            Equal(UsageConsent.NotAsked, GameSettingsJson.Decode("{}"u8.ToArray()).UsageConsent);
            Equal(UsageConsent.Yes, GameSettingsJson.Decode("{\"sendUsageData\":true}"u8.ToArray()).UsageConsent);
            foreach (var consent in Enum.GetValues<UsageConsent>())
                Equal(consent, GameSettingsJson.Decode(GameSettingsJson.Encode(new GameSettings().WithUsageConsent(consent)))
                    .Reset(new()).UsageConsent);
        });
        Check("Windows crash attachment refuses legacy and hostile reports", () =>
        {
            ImmutableArray<byte> Bytes(string input) => [.. Encoding.UTF8.GetBytes(input)];
            Equal(WindowsCrashReport.Example, WindowsCrashReport.Parse(Bytes(WindowsCrashReport.Example), []));
            foreach (var line in new[] { "Command line: +seta name private", "000000000000000086D3",
                "Frame: C:\\Users\\private\\engine.exe+0xAA", "Module: 203.0.113.12 unknown",
                "Frame: private.dll+0xAA", "MAC address: AA-BB-CC-DD-EE-FF", "chat: private", "argv=private" })
                Equal<string?>(null, WindowsCrashReport.Parse(Bytes(WindowsCrashReport.Example.Replace("End", line + "\nEnd", StringComparison.Ordinal)), ["private"]));
            Equal<string?>(null, WindowsCrashReport.Parse(Bytes("Crash: test\n"), []));
            Equal<string?>(null, WindowsCrashReport.Parse([0xc3, 0xff], []));
            var envelope = Encoding.UTF8.GetString(CrashReporting.WindowsPreview(
                new("0.5.0", "Windows", "10.0.26100", "Lenovo ThinkPad T14"), DateTimeOffset.UnixEpoch).AsSpan());
            Equal(true, envelope.Contains(WindowsCrashReport.Example, StringComparison.Ordinal));
            Equal(false, envelope.Contains("windows_arch", StringComparison.Ordinal));
            Equal(false, envelope.Contains("\"dsn\"", StringComparison.Ordinal));
        });

        static void Usage(string name, Func<Task> test) => Check("usage " + name, () => test().GetAwaiter().GetResult());
    }

    private static ConfigDirectorySnapshot SignedUsageCatalogue()
    {
        using var fixture = Fixtures("directory");
        var root = fixture.RootElement;
        var index = new Uri(root.String("indexURL")!);
        var signature = new Uri(root.String("signatureURL")!);
        var row = root.GetProperty("cases").EnumerateArray().Single(item => item.String("id") == "signed");
        var source = new DirectoryFetcher();
        source.Responses[index] = new(ImmutableArray.Create(row.Bytes("index")), new Uri(row.String("finalURL")!), null);
        source.Responses[signature] = new(ImmutableArray.Create(row.Bytes("signature")), signature, null);
        var trust = new ConfigDirectoryConfiguration(index, signature, ImmutableArray.Create(root.Bytes("publicKey")));
        return Load(new ConfigDirectory(trust, new DirectoryStorage(), source)).Snapshot!;
    }

    private static string? Name(JsonElement item) => item.GetProperty("event").GetString();
    private static JsonElement Props(JsonElement item) => item.GetProperty("properties");

    private static JsonElement[] Events(IEnumerable<byte[]> requests) =>
        requests.SelectMany(body =>
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("batch").EnumerateArray().Select(value => value.Clone()).ToArray();
        }).ToArray();

    private static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
            await Task.Delay(10, deadline.Token);
    }

    private sealed class UsageFixture
    {
        internal readonly MemoryPlatform Disk = new("reports");
        internal readonly UsageClock Clock = new();
        internal readonly ConcurrentQueue<byte[]> Captures = new();
        internal UsageConsent Saved;
        internal UsageFacts Facts = WindowsFacts;
        internal Guid? ForcedId;
        internal Func<CancellationToken, ValueTask<bool>> Send = _ => ValueTask.FromResult(true);

        internal async Task<UsageData> Start(bool setup = true, bool available = true)
        {
            var usage = new UsageData(Disk, available ? LoopbackUsage : null, () => Facts, (_, bytes, token) =>
            {
                Captures.Enqueue(bytes);
                return Send(token);
            }, Clock, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), () =>
            {
                var id = ForcedId ?? Guid.NewGuid();
                ForcedId = null;
                return id;
            });
            await usage.InitializeAsync(Saved, setup);
            return usage;
        }

        internal Task Persist(UsageConsent consent) { Saved = consent; return Task.CompletedTask; }
    }

    private sealed class UsageClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
