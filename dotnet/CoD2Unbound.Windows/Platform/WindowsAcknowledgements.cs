namespace CoD2Unbound.Windows.Platform;

internal sealed record WindowsNotice(string Name, string Author, string Licence, string Role, params string[] Resources);

internal static class WindowsAcknowledgements
{
    internal static readonly WindowsNotice[] Notices =
    [
        new("opencod2", "opencod2 authors, including riicchhaarrd", "No licence declared", "The reconstructed engine this port is built on.", "opencod2.txt", "provenance.txt"),
        new("SDL 2.30.9", "SDL contributors", "zlib", "Windows, input and sound for the game.", "SDL2.txt"),
        new("curl 8.14.1", "Daniel Stenberg and curl contributors", "curl", "Downloads for the game over Windows TLS.", "curl.txt"),
        new("zlib 1.3.1", "Jean-loup Gailly and Mark Adler", "zlib", "Compression used by the engine.", "zlib.txt"),
        new("MinGW-w64 runtime", "MinGW-w64 contributors", "Component licences", "The Windows C runtime and winpthreads.", "mingw-w64.txt"),
        new("GCC runtime", "Free Software Foundation and GCC contributors", "GPL-3.0 with GCC Runtime Library Exception",
            "The compiler’s C and C++ runtime.", "gcc-exception.txt", "gcc-gpl.txt"),
        new("minimp3", "lieff and contributors", "CC0-1.0", "Decodes MP3 in the engine.", "minimp3.txt"),
        new("Speex", "Jean-Marc Valin, Xiph.Org and contributors", "BSD-3-Clause", "The inherited voice codec.", "speex.txt"),
        new("Windows App SDK", "Microsoft", "MIT", "The launcher’s window and controls.", "windows-app-sdk.txt"),
        new("Big Shoulders Stencil Display", "Big Shoulders Project authors", "OFL-1.1", "Display typeface.", "big-shoulders.txt"),
        new("Courier Prime", "John August and Quote-Unquote Apps", "OFL-1.1", "Monospaced typeface.", "courier-prime.txt"),
        new("Fluent UI System Icons", "Microsoft", "MIT", "Launcher icons.", "fluent-icons.txt"),
        new("CoD2 Unbound", "Neoptolemos Kyriakou and contributors", "MIT", "The launcher and the project’s own changes.", "unbound.txt")
    ];

    internal static string Read(WindowsNotice notice) => string.Join("\n\n", notice.Resources.Select(ReadResource));

    internal static string ReadAll() => ReadResource("third-party.txt") + "\n\n" +
        string.Join("\n\n", Notices.Select(notice =>
            $"{notice.Name}\n{notice.Author} · {notice.Licence}\n{notice.Role}\n\n{Read(notice)}"));

    private static string ReadResource(string file)
    {
        using var stream = typeof(WindowsAcknowledgements).Assembly.GetManifestResourceStream("Notice." + file)
            ?? throw new IOException("This licence notice is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }
}
