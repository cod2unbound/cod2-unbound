using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum HostedReach
{
    [JsonStringEnumMemberName("network")] Network,
    [JsonStringEnumMemberName("listed")] Listed
}

public enum HostedRules
{
    [JsonStringEnumMemberName("public")] Public,
    [JsonStringEnumMemberName("zpam")] Zpam
}

public sealed record HostedServer(string Id, string Name, GameEdition Engine, HostedReach Reach, int Players, string JoinPassword,
    string AdminPassword, HostedRules Rules, string GameType, bool FriendlyFire, string MatchMode, ImmutableArray<string> Maps, int Port)
{
    public const int DefaultPort = 28960;
    [JsonIgnore]
    public ZpamLine ZpamLine => Engine == GameEdition.Cod2x ? ZpamLine.Four : ZpamLine.Three;
    [JsonIgnore]
    public int LastListedPort => Engine == GameEdition.Cod2x ? 28991 : 28963;
    [JsonIgnore]
    public bool InLocalList => Port >= DefaultPort && Port <= LastListedPort;

    public static HostedServer New(string? id = null, string name = "My Server", GameEdition edition = GameEdition.Cod2x) =>
        new(id ?? Guid.NewGuid().ToString(), name, edition, HostedReach.Network, 12, "", RandomPassword(),
            HostedRules.Public, "tdm", false, "sd:comp", ["mp_toujane", "mp_carentan", "mp_burgundy"], DefaultPort);

    public static string RandomPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 16);
    }

    public override string ToString() => $"HostedServer {{ Id = {Id}, Engine = {Engine}, Reach = {Reach}, Port = {Port} }}";
}

public static class HostedServerJson
{
    public static ImmutableArray<HostedServer> Read(ReadOnlySpan<byte> bytes)
    {
        var servers = JsonSerializer.Deserialize(bytes, HostedServerJsonContext.Default.ImmutableArrayHostedServer);
        if (servers.IsDefault || servers.Any(server => server is null || string.IsNullOrEmpty(server.Id) || server.Name is null ||
            server.Maps.IsDefault || server.Maps.Any(map => map is null) || server.GameType is null || server.MatchMode is null ||
            server.AdminPassword is null || server.JoinPassword is null || !Enum.IsDefined(server.Reach) ||
            !Enum.IsDefined(server.Rules) || !Enum.IsDefined(server.Engine)))
            throw new JsonException("The hosted server list is invalid.");
        return servers;
    }

    public static ImmutableArray<byte> Write(ImmutableArray<HostedServer> servers) =>
        ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(servers, HostedServerJsonContext.Default.ImmutableArrayHostedServer));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ImmutableArray<HostedServer>))]
internal sealed partial class HostedServerJsonContext : JsonSerializerContext;

public static class HostingCatalog
{
    public static ImmutableArray<(string Id, string Title)> GameTypes { get; } =
    [
        ("tdm", "Team Deathmatch"), ("dm", "Deathmatch"), ("sd", "Search & Destroy"), ("ctf", "Capture the Flag"),
        ("hq", "Headquarters")
    ];
    public static ImmutableArray<(string Id, string Title)> MatchModes { get; } =
    [
        ("sd:comp", "Search & Destroy · match"), ("sd:comp_mr15", "Search & Destroy · first to 16"),
        ("sd:comp_mr10", "Search & Destroy · first to 11"), ("sd:comp_2v2", "Search & Destroy · 2 v 2"),
        ("sd:comp_rifle", "Search & Destroy · rifles only"), ("sd:comp_lan", "Search & Destroy · LAN"),
        ("tdm:comp", "Team Deathmatch · match"), ("dm:comp", "Deathmatch · match"), ("ctf:comp", "Capture the Flag · match"),
        ("hq:comp", "Headquarters · match"), ("sd:pub", "Search & Destroy · public rules")
    ];
    public static ImmutableArray<string> Maps { get; } =
    [
        "mp_breakout", "mp_brecourt", "mp_burgundy", "mp_carentan", "mp_dawnville", "mp_decoy", "mp_downtown", "mp_farmhouse",
        "mp_harbor", "mp_leningrad", "mp_matmata", "mp_railyard", "mp_rhine", "mp_toujane", "mp_trainstation"
    ];
    public static string StockMap(string name) => name.EndsWith("_fix", StringComparison.Ordinal) ? name[..^4] : name;
    public static string GameTypeTitle(string id) => GameTypes.FirstOrDefault(item => item.Id == id).Title ?? id;
    public static string MatchModeTitle(string id) => MatchModes.FirstOrDefault(item => item.Id == id).Title ?? id;
}
