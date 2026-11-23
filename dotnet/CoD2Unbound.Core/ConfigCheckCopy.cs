using System.Collections.Immutable;

namespace CoD2Unbound.Core;

/// <summary>The checker edits its own copy of the reviewed bytes.</summary>
public sealed class ConfigCheckCopy(ConfigCheckReport report)
{
    private readonly HashSet<int> applied = [];
    public int AppliedCount => applied.Count;
    public bool HasEdits => applied.Count != 0;
    public int LineCount => report.Lines.Length;
    public int CommandCount => report.Commands;
    public ImmutableArray<ConfigCheckFinding> Findings => [.. report.Findings.Where(item => !applied.Contains(item.Id))];
    public ImmutableArray<ConfigCheckFinding> Fixable => [.. report.Fixable.Where(item => !applied.Contains(item.Id))];
    public ImmutableArray<byte> Bytes => report.Apply(applied);

    public ImmutableArray<ConfigCheckShownLine> Lines(bool markedOnly)
    {
        var lines = report.Show(applied);
        return markedOnly ? [.. lines.Where(line => line.Mark is not null)] : lines;
    }

    public void Fix(int id)
    {
        if (report.Findings.Any(item => item.Id == id && item.Fix is not null))
            applied.Add(id);
    }

    public void FixAll() => applied.UnionWith(report.Fixable.Select(item => item.Id));
    public void Undo() => applied.Clear();

    public static async ValueTask<ImmutableArray<byte>> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        int limit = new ConfigImportLimits().MaximumFileBytes;
        var bytes = new byte[limit + 1];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken).ConfigureAwait(false);
        if (count > limit)
            throw new ConfigImportException(ConfigImportFailure.TooLarge);
        return ImmutableArray.Create(bytes, 0, count);
    }

    public static async Task ExportAsync(string path, ImmutableArray<byte> bytes, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(directory, ".cod2-config-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsTemporary = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, useAsync: true))
            {
                ownsTemporary = true;
                await output.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }
    }
}
