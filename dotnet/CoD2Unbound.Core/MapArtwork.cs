using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;

namespace CoD2Unbound.Core;

public sealed record MapPixels(int Width, int Height, ImmutableArray<byte> Rgba);

public static class IwiImageDecoder
{
    public const int MaximumPixels = 4_194_304;
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    public static MapPixels Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 28 or > MaximumFileBytes || !bytes.StartsWith("IWi\u0005"u8) ||
            (bytes[5] & 12) != 0 || Cod2xFiles.Word(bytes, 10) != 1 || Cod2xFiles.Dword(bytes, 12) != bytes.Length)
            throw InvalidImage();
        int width = Cod2xFiles.Word(bytes, 6), height = Cod2xFiles.Word(bytes, 8), format = bytes[4];
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || width * height > MaximumPixels || format is not (1 or 11 or 12 or 13))
            throw InvalidImage();

        int blockSize = format == 11 ? 8 : 16;
        int Size(int w, int h) => format == 1 ? w * h * 4 : ((w + 3) / 4) * ((h + 3) / 4) * blockSize;
        int offset = 28;
        if ((bytes[5] & 2) == 0)
        {
            int level = 0;
            for (int extent = 1; extent < width || extent < height; extent *= 2)
                level++;
            for (int mip = level; mip >= 1; mip--)
                offset += Size(Math.Max(1, width >> mip), Math.Max(1, height >> mip));
        }
        if (offset + Size(width, height) != bytes.Length)
            throw InvalidImage();

        byte[] rgba = new byte[width * height * 4];
        if (format == 1)
        {
            var source = bytes[offset..];
            for (int pixel = 0; pixel < rgba.Length; pixel += 4)
            {
                rgba[pixel] = source[pixel + 2];
                rgba[pixel + 1] = source[pixel + 1];
                rgba[pixel + 2] = source[pixel];
                rgba[pixel + 3] = source[pixel + 3];
            }
        }
        else
        {
            int blocksWide = (width + 3) / 4;
            for (int row = 0; row < (height + 3) / 4; row++)
                for (int col = 0; col < blocksWide; col++)
                    Block(bytes.Slice(offset + (row * blocksWide + col) * blockSize, blockSize),
                        rgba, col * 4, row * 4, width, height, format);
        }
        return new(width, height, [.. rgba]);
    }

    private static void Block(ReadOnlySpan<byte> block, Span<byte> rgba, int x0, int y0, int width, int height, int format)
    {
        int color = format == 11 ? 0 : 8;
        int first = Cod2xFiles.Word(block, color), second = Cod2xFiles.Word(block, color + 2);
        bool transparent = format == 11 && first <= second;
        Span<byte> colors = stackalloc byte[12];
        Rgb565(first, colors[..3]);
        Rgb565(second, colors.Slice(3, 3));
        for (int channel = 0; channel < 3; channel++)
        {
            int a = colors[channel], b = colors[channel + 3];
            colors[channel + 6] = (byte)(transparent ? (a + b) / 2 : (2 * a + b) / 3);
            colors[channel + 9] = (byte)(transparent ? 0 : (a + 2 * b) / 3);
        }
        uint indices = Cod2xFiles.Dword(block, color + 4);
        Span<byte> alpha = stackalloc byte[8];
        ulong alphaIndices = 0;
        if (format == 13)
        {
            int a = block[0], b = block[1];
            alpha[0] = (byte)a;
            alpha[1] = (byte)b;
            if (a > b)
            {
                for (int i = 1; i <= 6; i++)
                    alpha[i + 1] = (byte)(((7 - i) * a + i * b) / 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                    alpha[i + 1] = (byte)(((5 - i) * a + i * b) / 5);
                alpha[6] = 0;
                alpha[7] = 255;
            }
            for (int index = 0; index < 6; index++)
                alphaIndices |= (ulong)block[2 + index] << (index * 8);
        }

        for (int index = 0; index < 16; index++)
        {
            int x = x0 + index % 4, y = y0 + index / 4;
            if (x >= width || y >= height)
                continue;
            int code = (int)(indices >> (index * 2) & 3), destination = (y * width + x) * 4;
            colors.Slice(code * 3, 3).CopyTo(rgba[destination..]);
            rgba[destination + 3] = format switch
            {
                11 => transparent && code == 3 ? (byte)0 : (byte)255,
                12 => (byte)(((block[index / 2] >> ((index % 2) * 4)) & 15) * 17),
                _ => alpha[(int)(alphaIndices >> (index * 3) & 7)]
            };
        }
    }

    private static void Rgb565(int endpoint, Span<byte> output)
    {
        int red = endpoint >> 11 & 31, green = endpoint >> 5 & 63, blue = endpoint & 31;
        output[0] = (byte)((red << 3) | (red >> 2));
        output[1] = (byte)((green << 2) | (green >> 4));
        output[2] = (byte)((blue << 3) | (blue >> 2));
    }

    private static InvalidDataException InvalidImage() => new("Invalid IWI image.");
}

/// <summary>Reads untrusted map art through one verified, seekable IWD handle.</summary>
public sealed class IwdArtworkArchive
{
    private sealed record Entry(byte[] Name, int Flags, int Method, uint Crc, uint Compressed, uint Expanded, uint Offset);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream source;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long length;
    private readonly uint directory;

    public IwdArtworkArchive(Stream source)
    {
        if (!source.CanRead || !source.CanSeek || source.Length is < 22 or >= uint.MaxValue)
            throw InvalidArchive();
        this.source = source;
        length = source.Length;
        int tailLength = (int)Math.Min(length, 65_557);
        long tailStart = length - tailLength;
        byte[] tail = ReadAt(tailStart, tailLength);
        int trailer = tail.Length - 22;
        while (trailer >= 0 && (Cod2xFiles.Dword(tail, trailer) != 0x06054b50 ||
            trailer + 22 + Cod2xFiles.Word(tail, trailer + 20) != tail.Length))
            trailer--;
        if (trailer < 0 || Cod2xFiles.Word(tail, trailer + 4) != 0 || Cod2xFiles.Word(tail, trailer + 6) != 0 ||
            Cod2xFiles.Word(tail, trailer + 8) != Cod2xFiles.Word(tail, trailer + 10))
            throw InvalidArchive();
        int count = Cod2xFiles.Word(tail, trailer + 10);
        uint size = Cod2xFiles.Dword(tail, trailer + 12);
        directory = Cod2xFiles.Dword(tail, trailer + 16);
        if (count > 32_768 || size > 16 * 1024 * 1024 || (long)directory + size != tailStart + trailer)
            throw InvalidArchive();

        byte[] central = ReadAt(directory, (int)size);
        int offset = 0;
        for (int index = 0; index < count; index++)
        {
            if (offset > central.Length - 46 || Cod2xFiles.Dword(central, offset) != 0x02014b50)
                throw InvalidArchive();
            int nameLength = Cod2xFiles.Word(central, offset + 28);
            int next = offset + 46 + nameLength + Cod2xFiles.Word(central, offset + 30) + Cod2xFiles.Word(central, offset + 32);
            if (nameLength is < 1 or > 1024 || next > central.Length || Cod2xFiles.Word(central, offset + 34) != 0)
                throw InvalidArchive();
            var name = central.AsSpan(offset + 46, nameLength);
            string? key = null;
            try { key = StrictUtf8.GetString(name); }
            catch (DecoderFallbackException) { }

            if (key is not null && key.StartsWith("images/loadscreen_mp_", StringComparison.OrdinalIgnoreCase) &&
                key.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase))
            {
                var entry = new Entry(name.ToArray(), Cod2xFiles.Word(central, offset + 8), Cod2xFiles.Word(central, offset + 10),
                    Cod2xFiles.Dword(central, offset + 16), Cod2xFiles.Dword(central, offset + 20),
                    Cod2xFiles.Dword(central, offset + 24), Cod2xFiles.Dword(central, offset + 42));
                if (!entries.TryAdd(key, entry))
                    throw InvalidArchive();
            }
            offset = next;
        }
        if (offset != central.Length)
            throw InvalidArchive();
    }

    public bool Contains(string imageName) => entries.ContainsKey(imageName);

    public byte[] Image(string imageName)
    {
        if (!entries.TryGetValue(imageName, out var entry))
            throw new FileNotFoundException("The IWD does not contain that loading screen.");
        if ((entry.Flags & 0x41) != 0 || entry.Method is not (0 or 8) ||
            entry.Compressed is < 1 or > IwiImageDecoder.MaximumFileBytes || entry.Expanded is < 1 or > IwiImageDecoder.MaximumFileBytes ||
            (long)entry.Offset + 30 > directory || source.Length != length)
            throw InvalidArchive();
        byte[] local = ReadAt(entry.Offset, 30);
        if (Cod2xFiles.Dword(local, 0) != 0x04034b50 || Cod2xFiles.Word(local, 6) != entry.Flags ||
            Cod2xFiles.Word(local, 8) != entry.Method || Cod2xFiles.Word(local, 26) != entry.Name.Length)
            throw InvalidArchive();
        if ((entry.Flags & 8) == 0 && (Cod2xFiles.Dword(local, 14) != entry.Crc || Cod2xFiles.Dword(local, 18) != entry.Compressed ||
            Cod2xFiles.Dword(local, 22) != entry.Expanded))
            throw InvalidArchive();
        long nameOffset = (long)entry.Offset + 30;
        long payload = nameOffset + entry.Name.Length + Cod2xFiles.Word(local, 28);
        if (payload + entry.Compressed > directory || !ReadAt(nameOffset, entry.Name.Length).AsSpan().SequenceEqual(entry.Name))
            throw InvalidArchive();

        byte[] data = ReadAt(payload, (int)entry.Compressed);
        if (entry.Method == 8)
        {
            using var compressed = new DeflatePayload(data);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            data = new byte[entry.Expanded];
            try
            {
                inflater.ReadExactly(data);
                if (inflater.ReadByte() != -1 || compressed.Position != compressed.Length)
                    throw InvalidArchive();
            }
            catch (EndOfStreamException) { throw InvalidArchive(); }
        }
        if (data.Length != entry.Expanded || ZipCrc32(data) != entry.Crc)
            throw InvalidArchive();
        return data;
    }

    // DeflateStream can buffer ahead. Feed the final bytes individually so the
    // stream position can reject bytes left after the deflate block.
    private sealed class DeflatePayload(byte[] payload) : MemoryStream(payload, writable: false)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Limit(buffer.Length)]);
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Limit(count));
        private int Limit(int count) => (int)Math.Min(count, Math.Max(1, Length - Position - 64));
    }

    private byte[] ReadAt(long offset, int count)
    {
        var bytes = new byte[count];
        try
        {
            source.Position = offset;
            source.ReadExactly(bytes);
            return bytes;
        }
        catch (EndOfStreamException) { throw InvalidArchive(); }
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(index =>
    {
        uint value = (uint)index;
        for (int bit = 0; bit < 8; bit++)
            value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320u;
        return value;
    })];

    private static uint ZipCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
            crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

    private static InvalidDataException InvalidArchive() => new("Invalid map artwork archive.");
}

public static class MapArtwork
{
    public static ImmutableArray<string> ArchiveOrder { get; } =
        [.. GameArchives.Required.Skip(9).Take(1), .. GameArchives.Required.Reverse().Where(path => path != GameArchives.Required[9])];

    public static (double Width, double Height) DisplaySize(int width, int height) =>
        (width == height ? width * 4.0 / 3 : width, height);

    public static int NormalizeVariation(int value) => ((value % 13) + 13) % 13;
    public static string? ImageName(string map) => MapCatalog.Key(map) is { } key ? $"images/loadscreen_mp_{key}.iwi" : null;

    public static MapPixels? Find(string map, IEnumerable<Func<Stream>> openArchives)
    {
        if (ImageName(map) is not { } name)
            return null;
        foreach (var open in openArchives)
        {
            try
            {
                using var stream = open();
                var archive = new IwdArtworkArchive(stream);
                if (archive.Contains(name))
                    return IwiImageDecoder.Decode(archive.Image(name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return null;
    }
}
