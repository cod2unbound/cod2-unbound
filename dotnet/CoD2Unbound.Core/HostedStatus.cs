using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed record HostedStatusPlayer(int Id, string Name, int Score, int Ping);
public sealed partial record HostedStatus(string Map, string GameType, int MaxPlayers, ImmutableArray<HostedStatusPlayer> Players,
    string? MatchInfo)
{
    internal static string Plain(string text) => ColorCodes().Replace(text, "");

    public static HostedStatus? Parse(string response)
    {
        string[] lines = response.Split('\n');
        if (lines.Length < 2 || !lines[0].EndsWith("statusResponse", StringComparison.Ordinal))
            return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] pairs = lines[1].Split('\\');
        for (int i = 1; i + 1 < pairs.Length; i += 2)
            fields[pairs[i]] = pairs[i + 1];
        var players = ImmutableArray.CreateBuilder<HostedStatusPlayer>();
        for (int i = 2; i < lines.Length; i++)
        {
            string[] words = lines[i].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 3 && TryInt(words[0], out int score) && TryInt(words[1], out int ping))
                players.Add(new(i - 2, words[2].Trim('"'), score, ping));
        }

        string? raw = fields.GetValueOrDefault("_match_info") ?? fields.GetValueOrDefault("_match_round");
        string? match = raw is null ? null : new string(Plain(raw).Where(ch => ch >= ' ' && ch != '\u007f').ToArray()).Trim();
        if (match is { Length: > 0 })
        {
            int[] clusters = StringInfo.ParseCombiningCharacters(match);
            if (clusters.Length > 60)
                match = match[..clusters[60]];
        }
        TryInt(fields.GetValueOrDefault("sv_maxclients"), out int max);
        return new(fields.GetValueOrDefault("mapname", ""), fields.GetValueOrDefault("g_gametype", ""), max, players.ToImmutable(),
            string.IsNullOrEmpty(match) ? null : match);
    }

    [GeneratedRegex(@"\^[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex ColorCodes();

    private static bool TryInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}

public sealed class HostedAdminQueue(IUdpTransport transport, TimeProvider? timeProvider = null)
{
    private readonly Lock gate = new();
    private Task tail = Task.CompletedTask;

    public Task<bool> NextMapAsync(HostedServer server, Func<bool> ownsServer, CancellationToken cancellationToken = default) =>
        EnqueueAsync(server, "map_rotate", ownsServer, cancellationToken);

    public Task<bool> KickAsync(HostedServer server, HostedStatusPlayer player, Func<bool> ownsServer,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(server, $"kick \"{HostingConfig.Safe(HostedStatus.Plain(player.Name), 32)}\"", ownsServer, cancellationToken);

    private async Task<bool> EnqueueAsync(HostedServer server, string command, Func<bool> ownsServer, CancellationToken cancellationToken)
    {
        string password = HostingConfig.Password(server.AdminPassword);
        if (server.Port is < 1025 or > 65535 || password.Length == 0)
            return false;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = tail;
            tail = completion.Task;
        }
        try
        {
            await previous.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownsServer())
                return false;

            var bytes = Encoding.UTF8.GetBytes($"rcon {password} {command}");
            try
            {
                await transport.ExchangeAsync(new(new("127.0.0.1", server.Port), [.. WireParser.Marker, .. bytes],
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 16_384), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Keep the engine's rate-limit window even if cancellation arrives after the datagram was sent.
                await Task.Delay(TimeSpan.FromMilliseconds(550), timeProvider ?? TimeProvider.System).ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            completion.SetResult();
        }
    }
}
