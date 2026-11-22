namespace CoD2Unbound.Core;

public static class ScrimMath
{
    public static double Linear(byte channel) => Decode(channel / 255.0);
    public static double Luminance(byte r, byte g, byte b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    public static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    public static double Encode(double value) => value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    public static double Decode(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    public static (double Low, double High)? Percentiles(ReadOnlySpan<byte> rgba, int width, int height,
        (double X, double Y, double Width, double Height) region, double lower = 0.02, double upper = 0.98)
    {
        if (width <= 0 || height <= 0 || 4d * width * height != rgba.Length ||
            !double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) ||
            !double.IsFinite(region.Height) || !double.IsFinite(lower) || !double.IsFinite(upper))
            return null;
        int x0 = (int)Math.Clamp(width * region.X, 0, width), x1 = (int)Math.Clamp(width * (region.X + region.Width), 0, width);
        int y0 = (int)Math.Clamp(height * region.Y, 0, height), y1 = (int)Math.Clamp(height * (region.Y + region.Height), 0, height);
        if (x1 <= x0 || y1 <= y0)
            return null;
        int step = Math.Max(1, (int)Math.Sqrt((x1 - x0) * (y1 - y0) / 4096.0));
        var values = new List<double>();
        for (int y = y0; y < y1; y += step)
        {
            for (int x = x0; x < x1; x += step)
            {
                int offset = (y * width + x) * 4;
                values.Add(Luminance(rgba[offset], rgba[offset + 1], rgba[offset + 2]));
            }
        }
        values.Sort();
        return (values[Index(lower)], values[Index(upper)]);

        int Index(double fraction) => (int)Math.Clamp(values.Count * fraction, 0, values.Count - 1);
    }

    public static double DarkScrim(double high, double text, double target = 4.5)
    {
        double allowed = (text + 0.05) / (target * 1.04) - 0.05;
        if (high <= allowed) return 0.35;
        if (allowed <= 0) return 0.92;
        return Math.Clamp(1 - Encode(allowed) / Encode(high), 0.35, 0.92);
    }

    public static double LightWash(double low, double text, double paper, double target = 4.5)
    {
        double needed = (text + 0.05) * target * 1.04 - 0.05;
        if (low >= needed) return 0.3;
        if (paper <= needed) return 0.95;
        return Math.Clamp((Encode(needed) - Encode(low)) / (Encode(paper) - Encode(low)), 0.3, 0.95);
    }
}
