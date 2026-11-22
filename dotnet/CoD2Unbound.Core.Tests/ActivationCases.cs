using CoD2Unbound.Core;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ActivationCases()
    {
        SetupLinkCases();
        foreach (var (input, edition, address, password) in new[]
        {
            ("cod2://LOCALhost", GameEdition.Original, "localhost:28960", null),
            ("cod2://198.51.100.7:28961/", GameEdition.Original, "198.51.100.7:28961", null),
            ("cod2x://connect/LOCALhost:28962/", GameEdition.Cod2x, "localhost:28962", null),
            ("COD2X://%2Bconnect+LOCALhost:28963/", GameEdition.Cod2x, "localhost:28963", null),
            ("cod2://CONNECT/localhost%3A28964", GameEdition.Original, "localhost:28964", null),
            ("cod2x://connect%20localhost%20password%20%22p%2Bquit%20%22", GameEdition.Cod2x,
                "localhost:28960", "p+quit "),
            ("cod2://password%20%22%22%20%2Bconnect%20.a..-:00001", GameEdition.Original, ".a..-:00001", null)
        })
        {
            Check("server activation accepts " + input, () =>
            {
                var link = ServerLink.Parse(input)!;
                Equal(edition, link.Edition);
                Equal(address, link.Target.Address);
                Equal(password, link.Target.Password);
                var handoff = LaunchLink.Direct(link.Target.Address, link.Target.Password ?? "");
                Equal(address, handoff.Address);
                Equal(password ?? "", handoff.Password);
                Equal(true, handoff.Url.StartsWith("cod2x://connect%20", StringComparison.Ordinal));
            });
        }

        foreach (string input in new[]
        {
            "cod2x://evil;quit", "https://example.com", "localhost:28960", "cod2:invalid",
            "cod2x://connect/localhost%3Bquit", "cod2x://connect%20host%20quit%20x",
            "cod2x://connect%20host%3A0", "cod2x://connect%20host%3A65536", "cod2x://connect%20host%00",
            "cod2x://connect%20[::1]", "cod2x://connect%20host%20password%20%22%3B%22", "cod2x://connect%Q0",
            "cod2x://connect%20host%20password%20a%20password%20b", "cod2x://connect%20host%20password%20%22a%22b",
            "https://connect/host", "cod2x://connect%20host%20password%20%22%C3%A9%22", "cod2x://connect%20host:289600",
            "cod2://localhost?password=secret", "cod2://localhost#quit", "cod2://user@host:28960",
            "cod2://host%0Aquit", "cod2://host%09", "cod2://host%253Bquit", "cod2://host%5cquit",
            "cod2x://connect+localhost+quit/", "cod2x://connect%20host%20%2Bset%20cl_allowDownload",
            "cod2x://connect%20host%20connect%20other", "cod2x://host:9999999999999999999999",
            "cod2://host%20", "cod2://host//", "cod2://host\t"
        })
            Check("server activation rejects " + input, () => Equal(null, ServerLink.Parse(input)));

        // The fixtures use MSG_WriteBitsCompress's initialized byte codes and CL_Record_f's packet layout.
        const string Original = "0a0000002c000000000000005b15a9c71ef11432f03c821d7b54889d9d05f6e261801d2b3c9acdce3e9ec53c363c7cc05f55f507ffffffffffffffff";
        const string Cod2x = "0a0000002c000000000000005b15a9c71ef11432f03c821d7b54889d9d05f6e261801d2b3c9acdce3e9ec53c36fc7ef25755fd01ffffffffffffffff";
        const string LegacyCod2x = "0a0000003a000000000000005b15a9c71ef11432f03c821d7b54889d9d05f6e261801d2b3c9acdce3e9ec53c363c7ce0d853ecc7b397de978e0d4f3538e0afaafa03ffffffffffffffff";
        const string Unsupported = "0a0000002c000000000000005b15a9c71ef11432f03c821d7b54889d9d05f6e261801d2b3c9acdce3e9ec53c363c1cebafaafa03ffffffffffffffff";
        Check("Original demo edition", () => Equal(GameEdition.Original, DemoEdition(Original)));
        Check("CoD2x demo edition", () => Equal(GameEdition.Cod2x, DemoEdition(Cod2x)));
        Check("protocol 118 with CoD2x marker", () => Equal(GameEdition.Cod2x, DemoEdition(LegacyCod2x)));
        Check("unsupported demo protocol", () => Equal<GameEdition?>(null, DemoEdition(Unsupported)));
        Check("truncated demo", () => Equal<GameEdition?>(null, DemoEdition(Original[..^20])));
        Check("negative packet size", () => Equal<GameEdition?>(null, DemoEdition(Original[..^16] + "0b000000feffffff")));
        Check("oversized packet", () => Equal<GameEdition?>(null, DemoEdition(Original[..8] + "01000200" + Original[16..])));
        Check("invalid game-state command", () => Equal<GameEdition?>(null, DemoEdition(Original[..24] + "00" + Original[26..])));

        static GameEdition? DemoEdition(string hex)
        {
            using var file = new MemoryStream(Convert.FromHexString(hex));
            return DemoRecording.ReadEdition(file);
        }
    }
}
