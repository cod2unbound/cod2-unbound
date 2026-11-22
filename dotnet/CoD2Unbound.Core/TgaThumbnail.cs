using System.Buffers.Binary;

namespace CoD2Unbound.Core;

public sealed record TgaThumbnail(int Width, int Height, byte[] Bgra)
{
    public static TgaThumbnail Read(Stream input, int longestSide)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(longestSide);
        // Accept large game screenshots while bounding both decoder work and thumbnail memory.
        const int maximumPixels = 8192 * 4096;
        Span<byte> header = stackalloc byte[18];
        input.ReadExactly(header);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        int imageType = header[2];
        int depth = header[16];
        int orientation = header[17];
        if (header[1] != 0 || imageType is not (2 or 10) || depth is not (24 or 32) ||
            width == 0 || height == 0 || (long)width * height > maximumPixels || (orientation & 0xc0) != 0)
            throw new IOException("Unsupported TGA screenshot.");

        Span<byte> identifier = stackalloc byte[header[0]];
        input.ReadExactly(identifier);
        int step = (Math.Max(width, height) - 1) / longestSide + 1;
        int outputWidth = (width - 1) / step + 1;
        int outputHeight = (height - 1) / step + 1;
        var bgra = new byte[outputWidth * outputHeight * 4];
        int bytesPerPixel = depth / 8;
        bool rightOrigin = (orientation & 0x10) != 0;
        bool topOrigin = (orientation & 0x20) != 0;
        Span<byte> packet = stackalloc byte[128 * 4];
        int pixelCount = width * height;
        for (int index = 0; index < pixelCount;)
        {
            int count = Math.Min(128, pixelCount - index);
            bool repeat = false;
            if (imageType == 10)
            {
                int control = input.ReadByte();
                if (control < 0)
                    throw new EndOfStreamException();
                count = (control & 0x7f) + 1;
                repeat = (control & 0x80) != 0;
                if (count > pixelCount - index)
                    throw new IOException("TGA packet exceeds the screenshot dimensions.");
            }
            input.ReadExactly(packet[..((repeat ? 1 : count) * bytesPerPixel)]);
            for (int i = 0; i < count; i++, index++)
            {
                int row = index / width;
                int column = index % width;
                if (!topOrigin)
                    row = height - 1 - row;
                if (rightOrigin)
                    column = width - 1 - column;
                if (row % step != 0 || column % step != 0)
                    continue;
                int source = repeat ? 0 : i * bytesPerPixel;
                int target = (row / step * outputWidth + column / step) * 4;
                packet.Slice(source, 3).CopyTo(bgra.AsSpan(target));
                // Game screenshots are opaque. Their fourth byte can contain unused render data.
                bgra[target + 3] = 255;
            }
        }
        return new(outputWidth, outputHeight, bgra);
    }
}
