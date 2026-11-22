namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void TgaCases()
    {
        foreach (int bits in new[] { 24, 32 })
        {
            byte[] red = bits == 24 ? [0, 0, 255] : [0, 0, 255, 255];
            byte[] green = bits == 24 ? [0, 255, 0] : [0, 255, 0, 0];
            Check($"uncompressed {bits}-bit TGA origins and opaque screenshot pixels", () =>
            {
                var image = TgaThumbnail.Read(new MemoryStream(Tga(2, bits, 2, 1, 0x30, [.. red, .. green])), 400);
                Equal(2, image.Width);
                Equal(1, image.Height);
                Sequence(new byte[] { 0, 255, 0, 255, 0, 0, 255, 255 }, image.Bgra);
            });
            Check($"run-length {bits}-bit TGA mixed packets and bottom origin", () =>
            {
                var image = TgaThumbnail.Read(new MemoryStream(Tga(10, bits, 2, 2, 0, [0x81, .. red, 1, .. green, .. red])), 400);
                Sequence(new byte[] { 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, image.Bgra);
            });
        }
        Check("TGA dimensions and image size are bounded", () => RejectTga(Tga(2, 32, 32000, 32000, 0, [])));
        Check("truncated TGA pixel data is rejected", () => RejectTga(Tga(2, 24, 2, 1, 0, [1, 2, 3])));
        Check("TGA RLE packet cannot overrun the image", () => RejectTga(Tga(10, 24, 1, 1, 0, [0x81, 1, 2, 3])));
        Check("TGA downsampling bounds output without changing orientation", () =>
        {
            var image = TgaThumbnail.Read(new MemoryStream(Tga(2, 24, 4, 2, 0x20,
                Enumerable.Repeat(new byte[] { 0, 0, 255 }, 8).SelectMany(pixel => pixel).ToArray())), 2);
            Equal(2, image.Width);
            Equal(1, image.Height);
            Sequence(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, image.Bgra);
        });
    }

    private static void RejectTga(byte[] bytes)
    {
        try { TgaThumbnail.Read(new MemoryStream(bytes), 400); }
        catch (IOException) { return; }
        throw new InvalidOperationException("The damaged TGA was accepted.");
    }

    private static byte[] Tga(int kind, int bits, int width, int height, byte descriptor, byte[] data) =>
        [0, 0, (byte)kind, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)bits, descriptor, .. data];
}
