using System.IO.Compression;
using System.Security.Cryptography;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void HostingPackageCases()
    {
        Check("zPAM installs only regular known files and publishes a complete verified stage", () =>
        {
            var samples = new[] { ("main/server.cfg", "map_rotate", 0), ("main/zpam336.iwd", "archive", 0),
                ("ignored/readme.txt", "notes", 0), ("main/extra.cfg", "unrecognized", 0) };
            using var archive = ZpamZip(samples);
            var release = ZpamSample(archive);
            var storage = new PackageStorage();
            Sync(ZpamPackage.InstallVerifiedAsync(release, archive, storage));
            Equal(true, storage.Published);
            Sequence(new[] { "server.cfg", "zpam336.iwd" }, storage.Files.Keys.Order(StringComparer.Ordinal));
            Sequence(ImportBytes("map_rotate"), storage.Files["server.cfg"].ToArray());
        });

        Check("zPAM staging failure leaves no installed package", () =>
        {
            using var archive = ZpamZip([("main/server.cfg", "text", 0), ("main/zpam336.iwd", "data", 0)]);
            var storage = new PackageStorage { FailOnCreate = "zpam336.iwd" };
            try { Sync(ZpamPackage.InstallVerifiedAsync(ZpamSample(archive), archive, storage)); }
            catch (IOException error) when (error.Message == "Fixture stage failed.")
            {
                Equal(false, storage.Published);
                Equal(0, storage.Files.Count);
                return;
            }
            throw new InvalidDataException("A failed stage reached publication.");
        });

        Check("zPAM rejects mismatches, missing package files and unsafe ZIP entries", () =>
        {
            using var clean = ZpamZip([("main/server.cfg", "text", 0), ("main/zpam.iwd", "archive", 0)]);
            var store = new PackageStorage();
            var release = ZpamSample(clean) with { Sha256 = new string('0', 64) };
            ZpamRejects(ZpamPackageFailure.Checksum, () => Sync(ZpamPackage.InstallVerifiedAsync(release, clean, store)));
            using var missing = ZpamZip([("main/server.cfg", "text", 0)]);
            ZpamRejects(ZpamPackageFailure.MissingFiles, () => Sync(ZpamPackage.InstallVerifiedAsync(ZpamSample(missing), missing, store)));
            using var special = ZpamZip([("main/server.cfg", "text", 0), ("main/zpam.iwd", "not a regular file", unchecked((int)0xa0000000))]);
            ZpamRejects(ZpamPackageFailure.UnsafeEntry, () => Sync(ZpamPackage.InstallVerifiedAsync(ZpamSample(special), special, store)));
            using var duplicate = ZpamZip([("main/server.cfg", "text", 0), ("main/zpam.iwd", "first", 0), ("main/ZPAM.iwd", "second", 0)]);
            ZpamRejects(ZpamPackageFailure.UnsafeEntry, () => Sync(ZpamPackage.InstallVerifiedAsync(ZpamSample(duplicate), duplicate, store)));
            Equal(false, store.Published);
            Equal(0, store.Files.Count);
        });
    }

    private static void ZpamRejects(ZpamPackageFailure reason, Action action)
    {
        try { action(); }
        catch (ZpamPackageException error) { Equal(reason, error.Failure); return; }
        throw new InvalidDataException($"Expected zPAM failure {reason}.");
    }

    private static MemoryStream ZpamZip(IEnumerable<(string Path, string Text, int Attributes)> entries)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text, attributes) in entries)
            {
                var entry = archive.CreateEntry(path);
                entry.ExternalAttributes = attributes;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }
        result.Position = 0;
        return result;
    }

    private static ZpamRelease ZpamSample(MemoryStream bytes) => new("3.36", "zpam336.zip",
        new("https://example.invalid/zpam336.zip"), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));

    private sealed class PackageStorage : IZpamPackageStorage
    {
        public Dictionary<string, MemoryStream> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Published { get; private set; }
        public string? FailOnCreate { get; init; }

        public ValueTask<IZpamPackageStage> BeginAsync(ZpamRelease release, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IZpamPackageStage>(new Stage(this));

        private sealed class Stage(PackageStorage owner) : IZpamPackageStage
        {
            public ValueTask<Stream> CreateAsync(string name, CancellationToken cancellationToken)
            {
                if (owner.FailOnCreate == name)
                    throw new IOException("Fixture stage failed.");
                var stream = new MemoryStream();
                owner.Files.Add(name, stream);
                return ValueTask.FromResult<Stream>(stream);
            }

            public ValueTask PublishAsync(CancellationToken cancellationToken)
            {
                owner.Published = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!owner.Published)
                    owner.Files.Clear();
                return ValueTask.CompletedTask;
            }
        }
    }
}
