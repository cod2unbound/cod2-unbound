using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public sealed class GameSessionLog(IEnumerable<string> secrets, string home)
{
    public const int SessionLimit = 10;
    public const int FileLimit = 4 * 1024 * 1024;
    public const int TotalLimit = 20 * 1024 * 1024;
    private const int LineLimit = 16_384;
    private ImmutableHashSet<string> secrets = secrets.Where(secret => secret.Length > 0)
        .ToImmutableHashSet(StringComparer.Ordinal);
    private readonly List<byte> pending = new();
    private int written;
    private bool dropping;
    private bool finished;

    public void Protect(string secret)
    {
        if (secret.Length != 0)
            ImmutableInterlocked.Update(ref secrets, values => values.Add(secret));
    }

    // Call from the single drain owned by the native adapter. The sink writes these bytes only
    // to the private verified file, with no second unfiltered output path.
    public ImmutableArray<byte> Append(ReadOnlySpan<byte> input, bool complete = false)
    {
        if (finished || written >= FileLimit)
            return [];
        var output = ImmutableArray.CreateBuilder<byte>();
        foreach (byte value in input)
        {
            if (value == 10)
            {
                WriteLine(output);
                if (written >= FileLimit)
                    break;
            }
            else if (!dropping && pending.Count < LineLimit)
                pending.Add(value);
            else
                dropping = true;
        }
        if (complete)
        {
            if ((dropping || pending.Count > 0) && written < FileLimit)
                WriteLine(output);
            finished = true;
        }
        return output.ToImmutable();
    }

    private void WriteLine(ImmutableArray<byte>.Builder output)
    {
        string text = dropping ? "[Long output line omitted]" : Filter(Encoding.UTF8.GetString(pending.ToArray()), Volatile.Read(ref secrets), home);
        byte[] line = Encoding.UTF8.GetBytes(text + "\n");
        if (line.Length > FileLimit - 64 - written)
        {
            output.AddRange("[Session log reached its 4 MiB limit; later output omitted.]\n"u8);
            written = FileLimit;
        }
        else
        {
            output.AddRange(line);
            written += line.Length;
        }
        dropping = false;
        pending.Clear();
    }

    public static string Filter(string line, IEnumerable<string> secrets, string home) =>
        CrashReporting.IsPrivate(line, secrets) ? "[Private connection or key details omitted]" :
            CrashReporting.RedactForSessionLog(line, home) ?? "[Private details omitted]";

    public static ImmutableArray<string> Prune(string prefix, string current, IEnumerable<MediaFile> files)
    {
        var removed = ImmutableArray.CreateBuilder<string>();
        int count = 1;
        long bytes = FileLimit;
        foreach (var file in files.Where(file => file.Name != current && MediaName.SafeComponent(file.Name) &&
            file.Name.StartsWith(prefix + "-", StringComparison.Ordinal) && file.Name.EndsWith(".log", StringComparison.Ordinal))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal))
        {
            long size = Math.Max(0, file.Bytes ?? 0);
            if (count >= SessionLimit || size > TotalLimit - bytes)
                removed.Add(file.Name);
            else
            {
                count++;
                bytes += size;
            }
        }
        return removed.ToImmutable();
    }
}
