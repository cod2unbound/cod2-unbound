using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public enum ConfigImportSourceKind { File, Folder, Paste, Drop, Https, Github, Gist, Localhost }
public sealed record ConfigImportSource(ConfigImportSourceKind Kind, Uri? OriginalUrl = null, Uri? RootUrl = null, Uri? FetchedUrl = null);
public sealed record ConfigImportFile(string Path, ConfigDocument Document);
public sealed record ConfigImportBundle(ConfigImportSource Source, string EntryPath, ImmutableArray<ConfigImportFile> Files,
    ImmutableArray<ConfigImportUnresolved> Unresolved);

public abstract record ConfigImportInput
{
    private ConfigImportInput() { }

    public sealed record File(Uri Url) : ConfigImportInput;
    public sealed record Folder(Uri Url, string EntryPath) : ConfigImportInput;
    public sealed record Paste(ImmutableArray<byte> Bytes) : ConfigImportInput;
    public sealed record Drop(ImmutableArray<byte> Bytes) : ConfigImportInput;
    public sealed record Remote(Uri Url) : ConfigImportInput;
}

public enum ConfigImportFailure { MissingEntry, TooLarge, NonText, InvalidUrl, OutsideRoot, Limits, HttpStatus }
public sealed class ConfigImportException(ConfigImportFailure failure, int? statusCode = null, Exception? innerException = null)
    : IOException(failure switch
    {
        ConfigImportFailure.MissingEntry => "The selected config could not be read.",
        ConfigImportFailure.TooLarge => "Config text exceeds the game's 65,535-byte buffer limit.",
        ConfigImportFailure.NonText => "Choose plain config text, rather than a web page or binary file.",
        ConfigImportFailure.InvalidUrl => "Use HTTPS, or HTTP on localhost or 127.0.0.1, without URL credentials.",
        ConfigImportFailure.OutsideRoot => "A dependency or redirect leaves the selected folder or repository.",
        ConfigImportFailure.Limits => "This import exceeds the bounded dependency review limits.",
        ConfigImportFailure.HttpStatus => $"The config server returned HTTP {statusCode}.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    }, innerException)
{
    public ConfigImportFailure Failure { get; } = failure;
    public int? StatusCode { get; } = statusCode;
}

public sealed record ConfigImportUnresolved(string FromFile, string RequestedName, ConfigImportFailure Failure, int? StatusCode = null);
public sealed record ConfigImportLimits(int MaximumFiles = 64, int MaximumFileBytes = 65_535, int MaximumTotalBytes = 262_140);
public sealed record ConfigImportLocalFile(Uri Root, string EntryPath);

public interface IConfigImportFiles
{
    // Obtain the root and leaf of the explicitly selected file, or validate the selected directory.
    ValueTask<ConfigImportLocalFile> ResolveFileAsync(Uri file, CancellationToken cancellationToken);
    ValueTask<Uri> ResolveFolderAsync(Uri folder, CancellationToken cancellationToken);

    // Read a regular file safely within the selected root. Bound the file before allocating.
    // Report missing, non-regular, unsafe and oversized files with the respective ConfigImportFailure.
    ValueTask<ImmutableArray<byte>> ReadAsync(Uri root, string relativePath, int maximumBytes, CancellationToken cancellationToken);
}

public sealed class ConfigImportLoader(IConfigImportCatalog catalog, IConfigImportFiles? localFiles = null,
    IConfigTextFetcher? fetcher = null, ConfigImportLimits? limits = null)
{
    private readonly ConfigImportLimits limits = limits ?? new();
    private static readonly string[] NonTextPrefixes = ["<!doctype", "<html", "<?xml", "%pdf-", "pk\u0003\u0004", "mz"];
    private static readonly byte[] Utf8Bom = [239, 187, 191];

    public async ValueTask<ConfigImportBundle> LoadAsync(ConfigImportInput input, ConfigDialect dialect,
        CancellationToken cancellationToken = default)
    {
        if (limits.MaximumFiles <= 0 || limits.MaximumFileBytes is < 1 or > 65_535 || limits.MaximumTotalBytes <= 0)
            throw new ConfigImportException(ConfigImportFailure.Limits);
        cancellationToken.ThrowIfCancellationRequested();
        var (source, entryPath, entry) = await LoadEntryAsync(input, cancellationToken).ConfigureAwait(false);
        Validate(entry);
        int total = entry.Length;
        if (total > limits.MaximumTotalBytes)
            throw new ConfigImportException(ConfigImportFailure.Limits);

        var files = ImmutableArray.CreateBuilder<ConfigImportFile>();
        files.Add(new(entryPath, new(entry.AsSpan(), dialect)));
        var unresolved = ImmutableArray.CreateBuilder<ConfigImportUnresolved>();
        var attempted = new HashSet<string>(StringComparer.Ordinal) { entryPath };
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            foreach (var value in ExecRequests(file.Document))
            {
                string requested = ConfigImportScript.Text(value.AsSpan());
                string? path = ConfigImportScript.ExecPath(value.AsSpan());
                if (path is null)
                {
                    unresolved.Add(new(file.Path, requested, ConfigImportFailure.OutsideRoot));
                    continue;
                }
                if (source.RootUrl is not { } root)
                {
                    unresolved.Add(new(file.Path, requested, ConfigImportFailure.MissingEntry));
                    continue;
                }
                if (!attempted.Add(path))
                    continue;
                if (attempted.Count > limits.MaximumFiles)
                    throw new ConfigImportException(ConfigImportFailure.Limits);

                try
                {
                    var data = root.IsFile
                        ? await ReadLocalAsync(root, path, cancellationToken).ConfigureAwait(false)
                        : (await ReadRemoteAsync(AppendPath(root, path), root, cancellationToken).ConfigureAwait(false)).Data;
                    Validate(data);
                    if (data.Length > limits.MaximumTotalBytes - total)
                        throw new ConfigImportException(ConfigImportFailure.Limits);
                    total += data.Length;
                    files.Add(new(path, new(data.AsSpan(), dialect)));
                }
                catch (ConfigImportException error) when (error.Failure != ConfigImportFailure.Limits)
                {
                    unresolved.Add(new(file.Path, requested, error.Failure, error.StatusCode));
                }
                catch (IOException error) when (error is not ConfigImportException)
                {
                    unresolved.Add(new(file.Path, requested, ConfigImportFailure.MissingEntry));
                }
            }
        }
        return new(source, entryPath, files.ToImmutable(), unresolved.ToImmutable());
    }

    private async ValueTask<(ConfigImportSource Source, string Path, ImmutableArray<byte> Bytes)> LoadEntryAsync(
        ConfigImportInput input, CancellationToken cancellationToken)
    {
        switch (input)
        {
            case ConfigImportInput.Paste paste:
                return (new(ConfigImportSourceKind.Paste), "import.cfg", paste.Bytes);
            case ConfigImportInput.Drop drop:
                return (new(ConfigImportSourceKind.Drop), "import.cfg", drop.Bytes);
            case ConfigImportInput.File file:
            {
                var local = localFiles ?? throw new ConfigImportException(ConfigImportFailure.MissingEntry);
                if (!file.Url.IsAbsoluteUri || !file.Url.IsFile)
                    throw new ConfigImportException(ConfigImportFailure.MissingEntry);
                var selection = await local.ResolveFileAsync(file.Url, cancellationToken).ConfigureAwait(false);
                if (selection.EntryPath.Length == 0 || selection.EntryPath is "." or ".." ||
                    selection.EntryPath.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
                    throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
                return (new(ConfigImportSourceKind.File, file.Url, selection.Root), selection.EntryPath,
                    await ReadLocalAsync(selection.Root, selection.EntryPath, cancellationToken).ConfigureAwait(false));
            }
            case ConfigImportInput.Folder folder:
            {
                var local = localFiles ?? throw new ConfigImportException(ConfigImportFailure.MissingEntry);
                if (!folder.Url.IsAbsoluteUri || !folder.Url.IsFile ||
                    ConfigImportScript.ExecPath(Encoding.UTF8.GetBytes(folder.EntryPath)) != folder.EntryPath)
                    throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
                var root = await local.ResolveFolderAsync(folder.Url, cancellationToken).ConfigureAwait(false);
                return (new(ConfigImportSourceKind.Folder, folder.Url, root), folder.EntryPath,
                    await ReadLocalAsync(root, folder.EntryPath, cancellationToken).ConfigureAwait(false));
            }
            case ConfigImportInput.Remote remote:
            {
                var (url, root, path, kind) = RemoteSource(remote.Url);
                var response = await ReadRemoteAsync(url, root, cancellationToken).ConfigureAwait(false);
                return (new(kind, remote.Url, root, response.FinalUrl), path, response.Data);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(input));
        }
    }

    private ValueTask<ImmutableArray<byte>> ReadLocalAsync(Uri root, string path, CancellationToken cancellationToken)
    {
        if (!root.IsAbsoluteUri || !root.IsFile)
            throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
        var local = localFiles ?? throw new ConfigImportException(ConfigImportFailure.MissingEntry);
        return local.ReadAsync(root, path, limits.MaximumFileBytes, cancellationToken);
    }

    private async ValueTask<ConfigTextResponse> ReadRemoteAsync(Uri url, Uri root, CancellationToken cancellationToken)
    {
        var http = fetcher ?? throw new ConfigImportException(ConfigImportFailure.MissingEntry);
        var request = new ConfigFetchRequest(url, limits.MaximumFileBytes, root);
        if (!request.Allows(url))
            throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
        ConfigTextResponse response;
        try { response = await http.FetchAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (ConfigFetchHttpException error) { throw new ConfigImportException(ConfigImportFailure.HttpStatus, error.StatusCode, error); }

        if (!request.Allows(response.FinalUrl))
            throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
        Validate(response.Data, response.ContentType);
        return response;
    }

    private void Validate(ImmutableArray<byte> data, string? contentType = null)
    {
        if (data.IsDefault)
            throw new ConfigImportException(ConfigImportFailure.MissingEntry);
        if (data.Length > limits.MaximumFileBytes)
            throw new ConfigImportException(ConfigImportFailure.TooLarge);
        if (contentType is not null && contentType.Split(';', 2)[0].ToLowerInvariant() is not
            ("text/plain" or "application/octet-stream" or "application/x-config" or "text/x-config"))
            throw new ConfigImportException(ConfigImportFailure.NonText);

        var text = data.AsSpan();
        foreach (byte value in text)
            if (value < 32 && value is not (9 or 10 or 13))
                throw new ConfigImportException(ConfigImportFailure.NonText);
        if (text.StartsWith(Utf8Bom))
            text = text[Utf8Bom.Length..];
        string sample = Encoding.UTF8.GetString(text[..Math.Min(text.Length, 256)]).Trim();
        if (NonTextPrefixes.Any(prefix => sample.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new ConfigImportException(ConfigImportFailure.NonText);
    }

    private List<ImmutableArray<byte>> ExecRequests(ConfigDocument entry)
    {
        var pending = new Queue<(ConfigDocument Document, int Depth)>();
        pending.Enqueue((entry, 0));
        var visited = new HashSet<ImmutableArray<byte>>(ConfigScriptBytes.Instance);
        var requests = new List<ImmutableArray<byte>>();
        int seen = 0;
        while (pending.TryDequeue(out var next))
        {
            var (document, depth) = next;
            if (depth > 16)
                throw new ConfigImportException(ConfigImportFailure.Limits);
            if (!visited.Add(document.Serialized))
                continue;

            foreach (var command in document.Commands)
            {
                if (++seen > 8192)
                    throw new ConfigImportException(ConfigImportFailure.Limits);
                var tokens = command.Tokens;
                if (tokens.IsEmpty)
                    continue;
                string name = tokens[0].Text.ToLowerInvariant();
                if (command.Kind == ConfigCommandKind.Exec && tokens.Length == 2)
                    requests.Add(tokens[1].Value);
                if (command.Kind == ConfigCommandKind.Bind && ConfigImportScript.Binding(command, document) is { } binding)
                    pending.Enqueue((new(binding.AsSpan(), document.Dialect), depth + 1));
                if (ConfigImportScript.Assignment(command, document, catalog, value => value == name) is { } assignment)
                    QueueIfExecutable(assignment.Value);
                if (tokens.Length > 2 && name is "toggle" or "togglep")
                    foreach (var token in tokens.AsSpan()[2..])
                        QueueIfExecutable(token.Value);
            }

            void QueueIfExecutable(ImmutableArray<byte> data)
            {
                if (ConfigImportScript.LooksExecutable(data, document.Dialect))
                    pending.Enqueue((new(data.AsSpan(), document.Dialect), depth + 1));
            }
        }
        return requests;
    }

    private static Uri AppendPath(Uri root, string path) =>
        new(root.AbsoluteUri.TrimEnd('/') + '/' + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    private static (Uri Url, Uri Root, string Path, ConfigImportSourceKind Kind) RemoteSource(Uri original)
    {
        if (!ConfigFetchRequest.IsPermitted(original))
            throw new ConfigImportException(ConfigImportFailure.InvalidUrl);

        var url = original;
        string[] segments = Segments(url);
        if (url.Host == "github.com")
        {
            if (segments.Length < 5 || segments[2] != "blob")
                throw new ConfigImportException(ConfigImportFailure.InvalidUrl);
            url = new("https://raw.githubusercontent.com/" + string.Join('/', segments.Take(2).Concat(segments.Skip(3))));
        }
        else if (url.Host == "gist.github.com")
        {
            if (segments.Length != 2)
                throw new ConfigImportException(ConfigImportFailure.InvalidUrl);
            url = new("https://gist.githubusercontent.com/" + string.Join('/', segments) + "/raw");
        }

        if (url.Host == "raw.githubusercontent.com")
        {
            segments = Segments(url);
            if (segments.Length < 4)
                throw new ConfigImportException(ConfigImportFailure.InvalidUrl);
            return (url, new("https://raw.githubusercontent.com/" + string.Join('/', segments.Take(3)) + '/'),
                Uri.UnescapeDataString(string.Join('/', segments.Skip(3))), ConfigImportSourceKind.Github);
        }

        if (url.Host == "gist.githubusercontent.com")
        {
            segments = Segments(url);
            if (segments.Length < 3 || segments[2] != "raw")
                throw new ConfigImportException(ConfigImportFailure.InvalidUrl);
            return (url, new("https://gist.githubusercontent.com/" + string.Join('/', segments.Take(3))),
                segments.Length >= 5 ? Uri.UnescapeDataString(string.Join('/', segments.Skip(4))) : "import.cfg", ConfigImportSourceKind.Gist);
        }

        return (url, ConfigFetchRequest.Parent(url), Uri.UnescapeDataString(segments.LastOrDefault() ?? ""),
            url.Scheme == Uri.UriSchemeHttp ? ConfigImportSourceKind.Localhost : ConfigImportSourceKind.Https);
    }

    private static string[] Segments(Uri uri) => uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
