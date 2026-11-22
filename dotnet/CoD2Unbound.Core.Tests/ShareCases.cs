using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ShareCases()
    {
        using var fixture = Fixtures("share");
        var root = fixture.RootElement;
        var catalog = new ConfigImportCatalogAdapter(ConfigCatalog.LoadBundled());
        foreach (var (row, index) in root.GetProperty("prepared").EnumerateArray().Select((row, index) => (row, index)))
        {
            Check($"Share preparation {index}", () =>
            {
                var prepared = Sync(ConfigShare.PrepareAsync(ImportBytes(row.String("input")!), catalog));
                Sequence(row.Bytes("bytes"), prepared.Bytes);
                Equal(row.Int("settings"), prepared.Settings);
                Equal(row.Int("binds"), prepared.Binds);
                Equal(row.Int("lines"), prepared.Lines);
                Equal(row.String("digest"), prepared.Digest);
                Sequence(row.Strings("reasons"), prepared.Reasons);
                Equal(row.String("problem"), prepared.Problem is null ? null : LowerFirst(prepared.Problem.ToString()!));
                var omitted = row.GetProperty("leftOut");
                Equal(omitted.GetArrayLength(), prepared.LeftOut.Length);
                for (int i = 0; i < prepared.LeftOut.Length; i++)
                {
                    Equal(omitted[i].String("shown"), prepared.LeftOut[i].Shown);
                    Equal(omitted[i].String("because"), prepared.LeftOut[i].Because);
                    Sequence(omitted[i].Strings("reasons"), prepared.LeftOut[i].Reasons);
                }
                var payload = ConfigShare.Request(prepared, "Fixture", ConfigDialect.Cod2x, "0.4.1-dev");
                var expected = row.GetProperty("body");
                Equal(expected.String("config"), payload.Config);
                Equal(expected.String("name"), payload.Name);
                Equal(expected.String("game"), payload.Game);
                Equal(expected.String("app"), payload.App);
                Equal(expected.Int("v"), payload.V);
                Equal(expected.GetProperty("left").Int("lines"), payload.Left.Lines);
                Sequence(expected.GetProperty("left").Strings("reasons"), payload.Left.Reasons);
            });
        }
        foreach (var row in root.GetProperty("names").EnumerateArray())
        {
            Check("Share credit name", () =>
            {
                Equal(row.String("name"), ConfigShareName.Valid(row.String("input")!));
                Equal(row.String("problem"), ConfigShareName.Problem(row.String("input")!));
            });
        }
        foreach (var row in root.GetProperty("shares").EnumerateArray())
        {
            Check($"Share response {row.Int("status")}", () =>
            {
                var reply = ConfigShareClient.ShareReply(row.Int("status"), ImportBytes(row.String("body")!));
                Equal(row.String("outcome"), LowerFirst(reply.Kind.ToString()));
            });
        }
        foreach (var row in root.GetProperty("withdrawals").EnumerateArray())
        {
            Check($"withdrawal response {row.Int("status")}", () =>
            {
                var reply = ConfigShareClient.WithdrawReply(row.Int("status"), ImportBytes(row.String("body")!));
                Equal(row.String("outcome"), LowerFirst(reply.Kind.ToString()));
            });
        }

        Check("Share preparation redacts credential commands in preview and payload", () =>
        {
            var input = ImportBytes("login sample-secret\nopenscriptmenu \"login new-secret\"\nseta password other-secret\n");
            var prepared = Sync(ConfigShare.PrepareAsync(input, catalog));
            Equal(0, prepared.Bytes.Length);
            Equal(3, prepared.LeftOut.Length);
            Equal(false, prepared.LeftOut.Any(line => line.Shown.Contains("-secret", StringComparison.Ordinal)));
        });

        Check("Share record persists token, uses case-insensitive ids, and keeps the last name", () =>
        {
            var storage = new ShareStorage { Bytes = ImmutableArray.Create(root.Bytes("records")) };
            var records = new ConfigShareRecords(storage);
            var saved = Sync(records.LoadAsync());
            var prior = saved.Record("MAIN")!;
            Equal(128L, prior.Request);
            Equal("Wren", saved.Name);
            Equal(false, prior.ToString().Contains(prior.Token, StringComparison.Ordinal));
            Equal("2026-11-21", ConfigShareRecords.Day(new DateTimeOffset(2026, 11, 22, 0, 30, 0, TimeSpan.FromHours(2))));
            Sync(records.RememberAsync("Alternate", prior with { Name = "Other" }));
            Sync(records.ForgetAsync("Main"));
            var after = Sync(new ConfigShareRecords(storage).LoadAsync());
            Equal(null, after.Record("Main"));
            Equal(prior.Token, after.Record("ALTERNATE")!.Token);
            Equal("Other", after.Name);
        });

        Check("successful Share keeps its receipt even if saving fails", () =>
        {
            var prepared = Sync(ConfigShare.PrepareAsync(ImportBytes(root.GetProperty("prepared")[0].String("input")!), catalog));
            var successful = root.GetProperty("shares")[0];
            var standIn = new ShareReplyStandIn { Reply = new(201, ImportBytes(successful.String("body")!)) };
            var storage = new ShareStorage { FailWrite = true };
            var records = new ConfigShareRecords(storage);
            var service = new ConfigShareService(new(standIn, new("http://127.0.0.1:9")), records);
            var publication = Sync(service.PublishAsync("Main", prepared, " Wren ", ConfigDialect.Cod2x, "0.4.1"));
            Equal(ConfigShareReplyKind.Sent, publication.Reply.Kind);
            Equal(false, publication.Saved);
            Equal(prepared.Digest, publication.Record!.Sha256);
            storage.FailWrite = false;
            Sync(records.RememberAsync("Main", publication.Record));
            Equal(publication.Record, Sync(records.LoadAsync()).Record("Main"));
        });

        Check("withdrawal keeps a failed or replaced receipt and clears a successful one", () =>
        {
            var storage = new ShareStorage { Bytes = ImmutableArray.Create(root.Bytes("records")) };
            var records = new ConfigShareRecords(storage);
            var standIn = new ShareReplyStandIn { Reply = new(503, ImportBytes("{\"error\":\"unavailable\"}")) };
            var client = new ConfigShareClient(standIn, new("http://127.0.0.1:9"));
            var service = new ConfigShareService(client, records);
            var saved = Sync(records.LoadAsync()).Record("Main")!;
            Equal(ConfigWithdrawReplyKind.Unreachable, Sync(service.WithdrawAsync("Main")).Reply.Kind);
            Equal(saved, Sync(records.LoadAsync()).Record("Main"));
            standIn.Reply = new(200, ImportBytes("{\"state\":\"withdrawn\"}"));
            standIn.OnPost = () => Sync(records.RememberAsync("Main", saved with { Request = 129, Url = new(ConfigShareClient.Pulls + "129") }));
            var raced = Sync(service.WithdrawAsync("Main"));
            Equal(ConfigWithdrawReplyKind.Withdrawn, raced.Reply.Kind);
            Equal(false, raced.Removed);
            Equal(129L, Sync(records.LoadAsync()).Record("Main")!.Request);
            standIn.OnPost = null;
            Equal(true, Sync(service.WithdrawAsync("Main")).Removed);
            Equal(null, Sync(records.LoadAsync()).Record("Main"));
            Equal("Wren", Sync(records.LoadAsync()).Name);
        });

        Check("Share catalogue credit line", () =>
        {
            var sample = new ConfigDirectoryEntry("fixture", "Wren", null, "2010", null, "Feel", "Story", [], null,
                "Wren", new(ConfigShareClient.Pulls + "128"),
                new("CoD2 Unbound", "2026-11-21", ConfigDirectoryAttribution.SelfPublished, "sample"),
                null, null, new("https://example.invalid/config.cfg"), new string('b', 64), 12, ConfigDirectoryPlatform.Native);
            Equal("Shared by its player from CoD2 Unbound on 21 November 2026", ConfigShareCredit.Line(sample));
            Equal(null, ConfigShareCredit.Line(sample with { SourceUrl = new("https://example.invalid/pull/128") }));
        });

        Check("Share and withdrawal loopback HTTP contract", () => ShareLoopback(root).GetAwaiter().GetResult());
    }

    private static async Task ShareLoopback(JsonElement root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = (IPEndPoint)server.LocalEndpoint;
        string baseUrl = $"http://127.0.0.1:{endpoint.Port}";
        using var transport = new HttpConfigShareTransport();
        var client = new ConfigShareClient(transport, new Uri(baseUrl));
        var prepared = await ConfigShare.PrepareAsync(ImportBytes(root.GetProperty("prepared")[0].String("input")!),
            new ConfigImportCatalogAdapter(ConfigCatalog.LoadBundled()), deadline.Token);

        string token = new('a', 64);
        var payload = ConfigShare.Request(prepared, "Fixture", ConfigDialect.Cod2x, "0.4.1");
        var share = client.ShareAsync(payload, deadline.Token).AsTask();
        using (var first = await ReadPost(server, deadline.Token))
        {
            Equal("POST /api/configs/share HTTP/1.1", first.Start);
            Equal(true, first.HasJson);
            Equal(false, first.HasCookie);
            using var body = JsonDocument.Parse(first.Body);
            var values = body.RootElement;
            Equal("Fixture", values.String("name"));
            Equal(prepared.Text, values.String("config"));
            Equal(false, values.String("config")!.Contains("sample-secret", StringComparison.Ordinal));
            Sequence(new[] { "app", "config", "game", "left", "name", "v" }, values.EnumerateObject().Select(prop => prop.Name).Order());
            await first.Reply(201, $"{{\"request\":128,\"url\":\"{ConfigShareClient.Pulls}128\",\"id\":\"fixture\",\"token\":\"{token}\"}}", deadline.Token,
                "Set-Cookie: fixture=ignore\r\n");
        }
        var sent = await share;
        Equal(ConfigShareReplyKind.Sent, sent.Kind);
        Equal(token, sent.Receipt!.Token);
        Equal(false, sent.Receipt.ToString().Contains(token, StringComparison.Ordinal));

        var withdraw = client.WithdrawAsync(128, token, deadline.Token).AsTask();
        using (var second = await ReadPost(server, deadline.Token))
        {
            Equal("POST /api/configs/withdraw HTTP/1.1", second.Start);
            Equal(true, second.HasJson);
            Equal(false, second.HasCookie);
            using var body = JsonDocument.Parse(second.Body);
            Equal(128, body.RootElement.Int("request"));
            Equal(token, body.RootElement.String("token"));
            await second.Reply(200, $"{{\"state\":\"removal\",\"url\":\"{ConfigShareClient.Pulls}130\"}}", deadline.Token);
        }
        Equal(ConfigWithdrawReplyKind.Removal, (await withdraw).Kind);

        var redirect = client.WithdrawAsync(128, token, deadline.Token).AsTask();
        using (var third = await ReadPost(server, deadline.Token))
            await third.Reply(307, "{}", deadline.Token, $"Location: {baseUrl}/do-not-follow\r\n");
        Equal(ConfigWithdrawReplyKind.Unreachable, (await redirect).Kind);
        Equal(false, server.Pending());

        var oversized = client.ShareAsync(payload, deadline.Token).AsTask();
        using (var fourth = await ReadPost(server, deadline.Token))
            await fourth.Reply(201, "{}", deadline.Token, declaredLength: 999_999);
        Equal(ConfigShareReplyKind.Unreachable, (await oversized).Kind);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Equal(ConfigWithdrawReplyKind.Cancelled, (await client.WithdrawAsync(128, token, cancelled.Token)).Kind);
    }

    private static async Task<LocalPost> ReadPost(TcpListener server, CancellationToken cancellationToken)
    {
        var socket = await server.AcceptTcpClientAsync(cancellationToken);
        try
        {
            using var input = new StreamReader(socket.GetStream(), Encoding.ASCII, leaveOpen: true);
            string start = await input.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await input.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
            {
                int split = header.IndexOf(':');
                headers[header[..split]] = header[(split + 1)..].Trim();
            }
            var body = new char[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
            await input.ReadBlockAsync(body, cancellationToken);
            return new(socket, start, headers["Content-Type"] == "application/json", headers.ContainsKey("Cookie"), new string(body));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class LocalPost(TcpClient socket, string start, bool hasJson, bool hasCookie, string body) : IDisposable
    {
        public string Start { get; } = start;
        public bool HasJson { get; } = hasJson;
        public bool HasCookie { get; } = hasCookie;
        public string Body { get; } = body;

        public ValueTask Reply(int status, string reply, CancellationToken cancellationToken, string extra = "", int? declaredLength = null)
        {
            int length = declaredLength ?? Encoding.UTF8.GetByteCount(reply);
            var wire = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {length}\r\nConnection: close\r\n{extra}\r\n{reply}");
            return socket.GetStream().WriteAsync(wire, cancellationToken);
        }

        public void Dispose() => socket.Dispose();
    }

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class ShareReplyStandIn : IConfigShareTransport
    {
        public required ConfigShareResponse Reply { get; set; }
        public Action? OnPost { get; set; }

        public ValueTask<ConfigShareResponse> PostAsync(Uri destination, ImmutableArray<byte> payload, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Equal("127.0.0.1", destination.Host);
            OnPost?.Invoke();
            return ValueTask.FromResult(Reply);
        }
    }

    private sealed class ShareStorage : IConfigShareStorage
    {
        public ImmutableArray<byte>? Bytes { get; set; }
        public string Root => "fixture";
        public bool FailWrite { get; set; }
        public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable>(new ShareLease());
        public ValueTask<ImmutableArray<byte>?> ReadAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Bytes);
        public ValueTask WriteAsync(string name, ImmutableArray<byte> data, CancellationToken cancellationToken)
        {
            Equal("shared-setups.json", name);
            if (FailWrite)
                throw new IOException("Fixture write failed.");
            Bytes = data;
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class ShareLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
