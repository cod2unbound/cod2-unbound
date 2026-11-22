namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SetupLinkCases()
    {
        foreach (var (input, id) in new (string, string?)[] {
            ("cod2unbound://setup/vargr", "vargr"), ("cod2unbound://setup/team-north-2/", "team-north-2"),
            ("COD2UNBOUND://SETUP/vargr", "vargr"), ("cod2unbound://setup/" + new string('a', 64), new string('a', 64)),
            ("cod2unbound://setup/" + new string('a', 65), null) })
            Check($"setup link {input}", () => Equal(id, SetupLink.Parse(input)));

        foreach (string input in new[] { "cod2unbound://setup/", "cod2unbound://setup", "cod2unbound://setup/-vargr", "cod2unbound://setup/Vargr",
            "cod2unbound://setup/var_gr", "cod2unbound://setup/vargr/extra", "cod2unbound://setup//vargr", "cod2unbound://setup/vargr//",
            "cod2unbound://setup/..", "cod2unbound://setup/%2e%2e", "cod2unbound://setup/var%67r", "cod2unbound://setup/vargr%2Fx",
            "cod2unbound://setup/vargr?import=1", "cod2unbound://setup/vargr#try", "cod2unbound://user@setup/vargr",
            "cod2unbound://user:pass@setup/vargr", "cod2unbound://setup:8080/vargr", "cod2unbound://connect/vargr",
            "cod2unbound://setups/vargr", "cod2unbound:setup/vargr", "cod2unbound:///setup/vargr", "cod2x://setup/vargr",
            "https://cod2unbound.com/setup/vargr", "cod2unbound://setup/vargr;quit", "cod2unbound://setup/vargr%0A",
            "cod2unbound://setup/vargr%00", "cod2unbound://evil.example/setup/vargr", "cod2unbound://setup/../vargr",
            "cod2unbound://setup/vargr?", "cod2unbound://setup/vargr#", "cod2unbound://set%75p/vargr", "cod2unbound://setup/vargr\n" })
            Check($"refused setup link {input.TrimEnd()}", () => Equal(null, SetupLink.Parse(input)));
    }
}
