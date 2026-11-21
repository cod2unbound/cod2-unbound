using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CoD2Unbound.Core;

public enum Cod2xPlacement { GameFolder, Placed, InPlace, Removed, None, Failed }

public interface ICod2xFiles
{
    // All app-managed paths are under the private engine home. Existing unrelated entries are
    // preserved. Reject reparse traversal and compare/publish through handles under one lease.
    IDisposable Acquire();
    ImmutableArray<byte>? ReadPrivate(string name, int maximumBytes);
    bool GameDataContains(string root, string name);
    bool SharesMainDirectory(string root);
    bool CompareAndReplace(string name, ImmutableArray<byte>? expected, ImmutableArray<byte>? updated);
}

public static class Cod2xFiles
{
    public const string Version = "1.4.6.8";
    public const int ReleaseSize = 2_577_745;
    public const string ReleaseSha256 = "c33f52037e67eaa948746a93967d27d8c70ab58623abab48d07803600a6a8555";
    public const string ArchiveSha256 = "fcb20f972cea3ed57eb2479976626fbe34bc1a58cea2d626135e379b51c2fa16";
    public const string ArchiveName = "iw_CoD2x_01.iwd";
    public const string StoredPath = "cod2x/" + ArchiveName;
    public const string PlacedPath = "main/" + ArchiveName;
    internal const int DllLimit = 256 * 1024 * 1024;
    internal const int ArchiveLimit = 64 * 1024 * 1024;
    public static Uri ReleaseUrl { get; } = new("https://github.com/callofduty2x/CoD2x/releases/download/v1.4.6.8/CoD2x_1.4.6.8_windows.zip");

    public static (bool Installed, string Message) Status(ICod2xFiles files, string dataRoot)
    {
        using var lease = files.Acquire();
        if (files.ReadPrivate(StoredPath, ArchiveLimit) is { } kept)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(kept.AsSpan()));
            return (true, hash == ArchiveSha256 ? $"Installed · {Version}" : "Installed · from your CoD2x folder");
        }
        return (false, dataRoot.Length > 0 && files.GameDataContains(dataRoot, PlacedPath) ? "Found in your game folder" : "Not installed");
    }

    public static void InstallRelease(ICod2xFiles files, Stream zip)
    {
        var data = ExtractDll(zip, ReleaseSize, ReleaseSha256);
        var archive = ExtractArchive(data);
        if (!Convert.ToHexStringLower(SHA256.HashData(archive.AsSpan())).Equals(ArchiveSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"The download didn’t match CoD2x {Version}, so it wasn’t used.");
        Keep(files, archive);
    }

    public static void InstallFromDll(ICod2xFiles files, ReadOnlySpan<byte> dll) => Keep(files, ExtractArchive(dll));

    private static void Keep(ICod2xFiles files, ImmutableArray<byte> archive)
    {
        using var lease = files.Acquire();
        if (!files.CompareAndReplace(StoredPath, files.ReadPrivate(StoredPath, ArchiveLimit), archive))
            throw new IOException("Couldn’t save the files on this Mac.");
    }

    public static void Remove(ICod2xFiles files)
    {
        using var lease = files.Acquire();
        if (files.ReadPrivate(StoredPath, ArchiveLimit) is not { } kept)
            return;
        var target = files.ReadPrivate(PlacedPath, ArchiveLimit);
        if ((Equal(kept, target) && !files.CompareAndReplace(PlacedPath, target, null)) ||
            !files.CompareAndReplace(StoredPath, kept, null))
            throw new IOException("Couldn’t remove the files.");
    }

    public static Cod2xPlacement Prepare(ICod2xFiles files, GameEdition edition, string dataRoot)
    {
        if (dataRoot.Length == 0)
            return Cod2xPlacement.None;
        try
        {
            using var lease = files.Acquire();
            if (files.SharesMainDirectory(dataRoot))
                return Cod2xPlacement.None;
            bool installed = files.GameDataContains(dataRoot, PlacedPath);
            var kept = files.ReadPrivate(StoredPath, ArchiveLimit);
            var target = files.ReadPrivate(PlacedPath, ArchiveLimit);
            if (edition == GameEdition.Original || installed)
            {
                if (!Equal(kept, target))
                    return installed ? Cod2xPlacement.GameFolder : Cod2xPlacement.None;
                return files.CompareAndReplace(PlacedPath, target, null)
                    ? installed ? Cod2xPlacement.GameFolder : Cod2xPlacement.Removed
                    : Cod2xPlacement.Failed;
            }
            if (kept is null)
                return Cod2xPlacement.None;
            if (Equal(kept, target))
                return Cod2xPlacement.InPlace;
            return target is null && files.CompareAndReplace(PlacedPath, null, kept) ? Cod2xPlacement.Placed : Cod2xPlacement.Failed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Cod2xPlacement.Failed; }
    }

    public static byte[] ExtractDll(Stream zip, int length, string sha256)
    {
        if (!zip.CanSeek || length is < 22 or > ArchiveLimit)
            throw InvalidRelease();
        zip.Position = 0;
        if (!ReleasePackage.VerifyArchive(zip, length, sha256))
            throw InvalidRelease();
        try
        {
            zip.Position = 0;
            byte[] package = new byte[length];
            zip.ReadExactly(package);
            var (offset, compressed, expanded, method) = DllEntry(package);
            using var payload = new MemoryStream(package, offset, compressed, writable: false);
            using Stream body = method == 0 ? payload : new DeflateStream(payload, CompressionMode.Decompress, leaveOpen: true);
            byte[] dll = new byte[expanded];
            body.ReadExactly(dll);
            if (body.ReadByte() != -1)
                throw InvalidRelease();
            return dll;
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { throw InvalidRelease(); }
    }

    private static (int Offset, int Compressed, int Expanded, int Method) DllEntry(byte[] bytes)
    {
        int position = bytes.Length - 22;
        int earliest = Math.Max(0, position - 65_535);
        while (position >= earliest && (Dword(bytes, position) != 0x06054b50 ||
            position + 22 + Word(bytes, position + 20) != bytes.Length))
            position--;
        if (position < earliest || Word(bytes, position + 4) != 0 || Word(bytes, position + 6) != 0 ||
            Word(bytes, position + 8) != Word(bytes, position + 10))
            throw InvalidRelease();
        uint offset = Dword(bytes, position + 16), size = Dword(bytes, position + 12);
        int count = Word(bytes, position + 10);
        if ((long)offset + size > position)
            throw InvalidRelease();
        int directory = (int)offset;
        int cursor = directory, limit = directory + (int)size;
        (int, int, int, int)? dll = null;
        for (int entry = 0; entry < count; entry++)
        {
            if (cursor > limit - 46 || Dword(bytes, cursor) != 0x02014b50)
                throw InvalidRelease();
            int nameLength = Word(bytes, cursor + 28);
            int next = cursor + 46 + nameLength + Word(bytes, cursor + 30) + Word(bytes, cursor + 32);
            if (next > limit)
                throw InvalidRelease();
            string name = Encoding.UTF8.GetString(bytes.AsSpan(cursor + 46, nameLength));
            if (name.Split(['/','\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                ?.Equals("mss32.dll", StringComparison.OrdinalIgnoreCase) == true)
            {
                int flags = Word(bytes, cursor + 8), method = Word(bytes, cursor + 10);
                uint compressed = Dword(bytes, cursor + 20), expanded = Dword(bytes, cursor + 24), local = Dword(bytes, cursor + 42);
                if (dll is not null || (flags & 0x41) != 0 || method is not (0 or 8) || expanded is < 64 or > DllLimit ||
                    (long)local + 30 > directory || Dword(bytes, (int)local) != 0x04034b50)
                    throw InvalidRelease();
                int start = (int)local + 30 + Word(bytes, (int)local + 26) + Word(bytes, (int)local + 28);
                if ((long)start + compressed > directory)
                    throw InvalidRelease();
                dll = (start, (int)compressed, (int)expanded, method);
            }
            cursor = next;
        }
        return dll ?? throw InvalidRelease();
    }

    public static ImmutableArray<byte> ExtractArchive(ReadOnlySpan<byte> dll)
    {
        int length = dll.Length;
        if (length is < 64 or > DllLimit || !dll.StartsWith("MZ"u8))
            throw NoArchive();
        uint pe = Dword(dll, 60);
        if (pe > length - 4 || !dll[(int)pe..].StartsWith("PE\0\0"u8))
            throw NoArchive();
        int start = -1, finish = -1, candidates = 0;
        for (int position = 0; position <= length - 22; position++)
        {
            if (dll[position] != 0x50 || Dword(dll, position) != 0x06054b50)
                continue;
            uint directorySize = Dword(dll, position + 12), directory = Dword(dll, position + 16);
            int end = position + 22 + Word(dll, position + 20);
            int count = Word(dll, position + 10);
            if (Word(dll, position + 4) != 0 || Word(dll, position + 6) != 0 || Word(dll, position + 8) != count ||
                (long)directorySize + directory > position - 4 || end > length)
                continue;
            int begin = position - (int)directorySize - (int)directory;
            if (count < 2 || Dword(dll, begin) != 0x04034b50 || end - begin > ArchiveLimit)
                continue;
            if (++candidates > 128)
                throw NoArchive();
            if (!IsArchive(dll.Slice(begin + (int)directory, (int)directorySize), count))
                continue;
            if (start >= 0)
                throw NoArchive();
            start = begin;
            finish = end;
        }
        return start >= 0 ? ImmutableArray.Create(dll[start..finish]) : throw NoArchive();
    }

    private static bool IsArchive(ReadOnlySpan<byte> central, int count)
    {
        int cursor = 0;
        long expanded = 0;
        bool menu = false, arrow = false;
        for (int entry = 0; entry < count; entry++)
        {
            if (cursor > central.Length - 46 || Dword(central, cursor) != 0x02014b50)
                return false;
            expanded += Dword(central, cursor + 24);
            int nameLength = Word(central, cursor + 28);
            if (cursor + 46 + nameLength > central.Length || expanded > ArchiveLimit)
                return false;
            if (nameLength < 256)
            {
                var name = central.Slice(cursor + 46, nameLength);
                int nul = name.IndexOf((byte)0);
                if (nul >= 0) name = name[..nul];
                string normalized = Encoding.Latin1.GetString(name).Replace('\\', '/').ToLowerInvariant();
                menu |= normalized == "ui_mp/main.menu";
                arrow |= normalized == "materials/radar_player_arrow";
            }
            cursor += 46 + nameLength + Word(central, cursor + 30) + Word(central, cursor + 32);
        }
        return menu && arrow && cursor <= central.Length;
    }

    internal static ushort Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    internal static uint Dword(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static bool Equal(ImmutableArray<byte>? left, ImmutableArray<byte>? right) =>
        left is { } a && right is { } b && a.AsSpan().SequenceEqual(b.AsSpan());
    private static InvalidDataException NoArchive() => new("This folder doesn’t have CoD2x installed.");
    private static InvalidDataException InvalidRelease() => new($"The download didn’t match CoD2x {Version}, so it wasn’t used.");
}
