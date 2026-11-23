using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed class WindowsHostingFiles(string root, bool loopbackOnly) : IHostingFiles, IZpamPackageStorage, IZpamDownloadSource
{
    private static readonly ReleaseDownload Remote = new(ReleaseDownload.CreateClient());
    private readonly string packages = Path.Join(root, "zpam");

    public async ValueTask<ImmutableArray<byte>> ListAsync(CancellationToken cancellationToken)
    {
#if UPDATE_FIXTURES
        if (FixtureFolder is { } fixture)
            return await new WindowsSettingsFolder(fixture).ReadAsync("releases.json", cancellationToken) ?? [];
#endif
        if (loopbackOnly)
            throw new IOException("The test launcher has no zPAM release feed.");
        return await Remote.ReadZpamListingAsync(cancellationToken);
    }

    public async ValueTask<Stream> DownloadAsync(ZpamRelease release, Action<long> progress, CancellationToken cancellationToken)
    {
#if UPDATE_FIXTURES
        if (FixtureFolder is { } fixture)
        {
            var fixtureStream = WindowsFiles.Open(fixture, release.Asset);
            try
            {
                if (fixtureStream.Length != release.Bytes)
                    throw new IOException("The zPAM fixture size differs from its release listing.");
                for (int part = 1; part <= 12; part++)
                {
                    await Task.Delay(900, cancellationToken);
                    progress(release.Bytes * part / 12);
                }
                return fixtureStream;
            }
            catch
            {
                await fixtureStream.DisposeAsync();
                throw;
            }
        }
#endif
        if (loopbackOnly)
            throw new IOException("The test launcher cannot download zPAM.");
        using var parent = new WindowsFiles.DirectoryHandles(packages, create: true);
        string path = Path.Join(packages, $".download-{Guid.NewGuid():N}");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await Remote.CopyZpamAsync(release, stream, progress, cancellationToken);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public string? InstalledVersion(ZpamLine line)
    {
        if (!Directory.Exists(packages))
            return null;
        using var parent = new WindowsFiles.DirectoryHandles(packages);
        char major = line == ZpamLine.Four ? '4' : '3';
        var files = new WindowsFiles();
        return Directory.EnumerateDirectories(packages)
            .Select(path => Path.GetFileName(path))
            .Where(name => name is { Length: 4 } && name[0] == major && name[1] == '.' &&
                char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3]))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault(name =>
            {
                string path = Path.Join(packages, name);
                return files.IsRegularFile(path, "server.cfg") &&
                    Directory.EnumerateFiles(path, "*.iwd").Any(file => IsArchive(Path.GetFileName(file)) &&
                        files.IsRegularFile(path, Path.GetFileName(file)));
            });
    }

    public bool HasPackage(ZpamLine line) => InstalledVersion(line) is not null;

#if UPDATE_FIXTURES
    private string? FixtureFolder => loopbackOnly ? Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_ZPAM_FOLDER") : null;
#endif

    public async ValueTask<string?> PrepareAsync(string serverId, string home, ZpamLine? package, CancellationToken cancellationToken)
    {
        string main = Path.Join(home, "main");
        using var directory = new WindowsFiles.DirectoryHandles(main, create: true);
        string? version = package is { } line ? InstalledVersion(line) ??
            throw new IOException("The installed zPAM package is missing.") : null;
        string? source = version is null ? null : Path.Join(packages, version);
        using var sourceDirectory = source is null ? null : new WindowsFiles.DirectoryHandles(source);
        string[] archives = source is null ? [] :
            Directory.EnumerateFiles(source, "*.iwd").Select(path => Path.GetFileName(path)).Where(IsArchive).ToArray();

        const string marker = "managed-zpam.json";
        var settings = new WindowsSettingsFolder(home);
        var saved = await settings.ReadAsync(marker, cancellationToken);
        string[] previous = saved is { } data ?
            JsonSerializer.Deserialize(data.AsSpan(), WindowsHostingFilesJson.Default.StringArray) ?? [] : [];
        if (previous.Length == 0 && archives.Length == 0)
            return null;
        // Record the union before touching any archives so an interrupted staging can be retried.
        await TrackAsync(previous.Union(archives, StringComparer.OrdinalIgnoreCase));
        foreach (string old in previous.Where(IsArchive).Except(archives, StringComparer.OrdinalIgnoreCase))
            File.Delete(WindowsFiles.Under(main, old));
        foreach (string archive in archives)
        {
            using var input = WindowsFiles.Open(source!, archive);
            string temporary = WindowsFiles.Under(main, $".zpam-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, cancellationToken);
                File.Move(temporary, WindowsFiles.Under(main, archive), overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        await TrackAsync(archives);
        return await ReadTemplateAsync(version, cancellationToken);

        ValueTask TrackAsync(IEnumerable<string> names) =>
            settings.WriteAsync(marker, [.. JsonSerializer.SerializeToUtf8Bytes(
                names.ToArray(), WindowsHostingFilesJson.Default.StringArray)], cancellationToken);
    }

    internal async ValueTask<string?> ReadTemplateAsync(string? version, CancellationToken cancellationToken)
    {
        if (version is null)
            return null;
        using var file = WindowsFiles.Open(Path.Join(packages, version), "server.cfg");
        if (file.Length > 1_048_576)
            throw new IOException("The zPAM configuration is too large.");
        using var reader = new StreamReader(file, Encoding.Latin1);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public ValueTask<IZpamPackageStage> BeginAsync(ZpamRelease release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IZpamPackageStage>(new Stage(packages, release.Version));
    }

    public async ValueTask<string> PublishExportAsync(string parent, string baseName, ImmutableDictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        await using var stage = new Stage(parent, baseName);
        foreach (var (name, text) in files)
        {
            await using var output = await stage.CreateAsync(name, cancellationToken);
            await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        }
        return stage.Publish(unique: true, cancellationToken);
    }

    private static bool IsArchive(string name) => name.StartsWith("zpam", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".iwd", StringComparison.OrdinalIgnoreCase) &&
        name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.');

    private sealed class Stage : IZpamPackageStage
    {
        private readonly string parent;
        private readonly string name;
        private readonly string temporary;
        private readonly WindowsFiles.DirectoryHandles handle;
        private bool published;

        internal Stage(string parent, string name)
        {
            this.parent = parent;
            this.name = name;
            handle = new(parent, create: true);
            temporary = WindowsFiles.Under(parent, $".hosting-{Guid.NewGuid():N}");
            try { Directory.CreateDirectory(temporary); }
            catch { handle.Dispose(); throw; }
        }

        public ValueTask<Stream> CreateAsync(string file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stage = new WindowsFiles.DirectoryHandles(temporary);
            return ValueTask.FromResult<Stream>(new FileStream(WindowsFiles.Under(temporary, file), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous));
        }

        public ValueTask PublishAsync(CancellationToken cancellationToken)
        {
            Publish(unique: false, cancellationToken);
            return ValueTask.CompletedTask;
        }

        internal string Publish(bool unique, CancellationToken cancellationToken)
        {
            for (int suffix = 1; ; suffix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = WindowsFiles.Under(parent, suffix == 1 ? name : $"{name}-{suffix}");
                try
                {
                    Directory.Move(temporary, target);
                    published = true;
                    return target;
                }
                catch (IOException) when (unique && (Directory.Exists(target) || File.Exists(target))) { }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { if (!published) Directory.Delete(temporary, recursive: true); }
            finally { handle.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class WindowsHostingFilesJson : JsonSerializerContext;
