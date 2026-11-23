using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

public static class KitFrames
{
    public static string Compare(string capture, string reference, string output)
    {
        byte[] raw = File.ReadAllBytes(capture);
        if (raw.Length < 18 || raw[1] != 0 || raw[2] != 2 || (raw[16] != 24 && raw[16] != 32))
            throw new InvalidDataException("Expected an uncompressed color TGA from the game.");
        int height = raw[14] + 256 * raw[15], width = raw[12] + 256 * raw[13];
        int stride = raw[16] / 8, offset = 18 + raw[0];
        if (width < 1 || height < 1 || (long)offset + (long)stride * width * height > raw.Length)
            throw new InvalidDataException("The game's TGA is incomplete.");

        using (var baseline = new Bitmap(reference))
        using (var pair = new Bitmap(width * 2, height))
        {
            if (baseline.Width != width || baseline.Height != height)
                throw new InvalidDataException("The game and comparison image have different sizes.");
            using (var graphics = Graphics.FromImage(pair))
                graphics.DrawImageUnscaled(baseline, width, 0);

            long sum = 0, squares = 0, changed = 0;
            int peak = 0;
            for (int y = 0; y < height; y++)
            {
                int row = (raw[17] & 32) != 0 ? y : height - 1 - y;
                for (int x = 0; x < width; x++)
                {
                    int index = offset + (row * width + x) * stride;
                    var current = Color.FromArgb(raw[index + 2], raw[index + 1], raw[index]);
                    pair.SetPixel(x, y, current);
                    var prior = baseline.GetPixel(x, y);
                    int red = Math.Abs(current.R - prior.R), green = Math.Abs(current.G - prior.G), blue = Math.Abs(current.B - prior.B);
                    int maximum = Math.Max(red, Math.Max(green, blue));
                    if (maximum > 16) changed++;
                    peak = Math.Max(peak, maximum);
                    sum += red + green + blue;
                    squares += red * red + green * green + blue * blue;
                }
            }
            pair.Save(output, ImageFormat.Png);
            double pixels = (double)width * height;
            return string.Format(CultureInfo.InvariantCulture,
                "RGB mean absolute delta={0:F3}, RMS={1:F3}, pixels with channel delta >16={2:F2}%, maximum={3}",
                sum / (3 * pixels), Math.Sqrt(squares / (3 * pixels)), changed * 100 / pixels, peak);
        }
    }
}
