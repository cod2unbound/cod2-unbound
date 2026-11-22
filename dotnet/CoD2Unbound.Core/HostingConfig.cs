using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CoD2Unbound.Core;

public static class HostingConfig
{
    public static int OtherPort(HostedServer server, Func<int, bool> free)
    {
        for (int port = HostedServer.DefaultPort; port <= server.LastListedPort; port++)
            if (port != server.Port && free(port))
                return port;
        int current = Math.Clamp(server.Port, 1025, 65535);
        for (int port = current + 1; port <= Math.Min(current + 50, 65535); port++)
            if (free(port))
                return port;
        return current < 65535 ? current + 1 : HostedServer.DefaultPort;
    }

    public static string Safe(string value, int limit = 128)
    {
        var text = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch is < ' ' or '\u007f' or '"' or ';' or >= '\u0100')
                continue;
            if (ch == '/' && text.Length > 0 && text[^1] == '/')
                continue;
            text.Append(ch);
        }
        string clean = text.ToString().Trim();
        return clean[..Math.Min(limit, clean.Length)].TrimEnd('\\');
    }

    public static string Password(string value) => Safe(value, limit: 32).Replace(" ", "", StringComparison.Ordinal).TrimEnd('\\');

    public static ImmutableArray<string> Maps(HostedServer server)
    {
        var maps = server.Maps.Where(name => Word(name) && name.Length <= 64).ToImmutableArray();
        return maps.IsEmpty ? ["mp_toujane"] : maps;
    }

    public static string GameType(HostedServer server)
    {
        string type = server.Rules == HostedRules.Zpam
            ? server.MatchMode.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "sd" : server.GameType;
        return HostingCatalog.GameTypes.Any(item => item.Id == type) ? type : "tdm";
    }

    public static string PamMode(HostedServer server)
    {
        string mode = server.MatchMode.Split(':', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "comp";
        return Word(mode) ? mode : "comp";
    }

    public static string Render(HostedServer server, string? zpamTemplate = null, bool passwords = true)
    {
        var lines = Lines(server, passwords);
        if (zpamTemplate is null || server.Rules != HostedRules.Zpam)
        {
            var text = new StringBuilder($"// {Safe(server.Name, 64)}: written by CoD2 Unbound. Lines starting with // are comments.\n");
            foreach (var (name, value, comment) in lines)
                text.Append($"// {comment}\nset {name} \"{value}\"\n");
            return text.Append("// Start on the first map of the rotation\nmap_rotate\n").ToString();
        }

        var managed = lines.ToDictionary(line => line.Name, StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        bool started = false;
        foreach (string raw in zpamTemplate.Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0].ToLowerInvariant() is "set" or "seta" or "sets" &&
                managed.TryGetValue(words[1], out var replacement))
            {
                if (emitted.Add(replacement.Name))
                    output.Add($"{words[0]} {replacement.Name} \"{replacement.Value}\"  // {replacement.Comment}");
            }
            else if (words.Length == 2 && words[0].Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                if (!started)
                    output.Add("map_rotate");
                started = true;
            }
            else
                output.Add(line);
        }

        var extra = lines.Where(item => !emitted.Contains(item.Name))
            .Select(item => $"set {item.Name} \"{item.Value}\"  // {item.Comment}");
        int mapStart = output.LastIndexOf("map_rotate");
        if (mapStart >= 0)
            output.InsertRange(mapStart, extra);
        else
            output.AddRange(extra);
        if (!started)
            output.Add("map_rotate");
        return string.Join('\n', output) + (output[^1].Length == 0 ? "" : "\n");
    }

    public static string FileName(HostedServer server)
    {
        string text = new(server.Name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        string file = string.Join('-', text.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return file.Length == 0 ? "cod2-server" : file[..Math.Min(file.Length, 48)];
    }

    public static ImmutableDictionary<string, string> Export(HostedServer server, string? zpamTemplate, ZpamRelease? zpam,
        bool passwords, bool matchSettings)
    {
        if (server.Rules == HostedRules.Zpam && !matchSettings)
            server = server with { Rules = HostedRules.Public, GameType = GameType(server) };
        string mode = server.Reach == HostedReach.Listed ? "2" : "1";
        string options = FormattableString.Invariant($"+set net_port {server.Port} +set dedicated {mode} +exec server.cfg");
        string linuxNote = server.Reach == HostedReach.Listed ? "" :
            "# The Linux server lists itself on the public master servers whatever \"dedicated\" says. README.txt has the way round it.\n";
        string cod2x = server.Engine == GameEdition.Cod2x ? " with CoD2x installed (https://github.com/callofduty2x/CoD2x)" : "";
        var readme = new StringBuilder(FormattableString.Invariant(
            $"{Safe(server.Name, 64)}: server files written by CoD2 Unbound\n\n1. Copy server.cfg into the main folder of a Call of Duty 2 install on the server, patched to 1.3{cod2x}.\n2. Start the server with start-linux.sh or start-windows.bat from the game folder.\n3. Open UDP port {server.Port} to the server.\n\nGame files are not included: use your own licensed copy.\n"));
        if (server.Engine == GameEdition.Cod2x)
            readme.Append("Who can join is decided by the CoD2x on that machine: a CoD2x server takes players on its own version or a newer one.\n\n");
        if (!passwords)
            readme.Append("Passwords are not included. Replace every CHANGE_ME in server.cfg before starting.\n\n");
        if (server.Rules == HostedRules.Zpam)
        {
            string version = zpam is null ? "" : " " + zpam.Version;
            readme.Append($"This server uses zPAM{version}, which is not included. Download {zpam?.Asset ?? "its archive"} from {ZpamRelease.ProjectUrl.AbsoluteUri}/releases and copy its .iwd files into main/ beside server.cfg.\n");
            readme.Append("A zPAM server asks CoD2x’s update service for newer versions, and players’ games fetch zPAM’s maps from cod2x.me.\n\n");
        }
        readme.Append(server.Reach == HostedReach.Listed
            ? "The server lists itself on the public master servers. On Windows, change dedicated to 1 in start-windows.bat to keep it unlisted. A Linux server always lists itself: see below.\n\n"
            : "On Windows this server stays unlisted: nothing is announced.\n\n");
        readme.Append("On Linux the game's server program ignores the dedicated setting and always reports to the public master server, so the server shows in everyone's server list. To keep a Linux server unlisted, add this line to /etc/hosts on that machine before you start it:\n\n127.0.0.1 cod2master.activision.com\n\n");
        if (server.Engine == GameEdition.Cod2x)
            readme.Append("A CoD2x server also reports to CoD2x's own master, master.cod2x.me.\n");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server.cfg"] = Render(server, zpamTemplate, passwords),
            ["start-linux.sh"] = $"#!/bin/sh\n# Run from the game folder, beside main/. server.cfg goes into main/.\n{linuxNote}./cod2_lnxded {options}\n",
            ["start-windows.bat"] = $"@echo off\r\nrem Run from the game folder, beside main\\. server.cfg goes into main\\.\r\nCoD2MP_s.exe {options}\r\n",
            ["README.txt"] = readme.ToString()
        }.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static List<(string Name, string Value, string Comment)> Lines(HostedServer server, bool passwords)
    {
        string type = GameType(server);
        string join = Password(server.JoinPassword);
        if (!passwords && join.Length > 0)
            join = "CHANGE_ME";
        string admin = passwords ? Password(server.AdminPassword) : "CHANGE_ME";
        var lines = new List<(string Name, string Value, string Comment)>
        {
            ("sv_hostname", Safe(server.Name, 64), "The name players see in the server list"),
            ("sv_maxclients", Math.Clamp(server.Players, 2, 64).ToString(CultureInfo.InvariantCulture), "How many players can be on the server at once"),
            ("g_password", join, "Password to join; empty for an open server"),
            ("rcon_password", admin, "Admin password, for controlling the server from inside the game; empty turns that off"),
            ("g_gametype", type, "Game type: " + HostingCatalog.GameTypeTitle(type))
        };
        if (server.Rules == HostedRules.Zpam)
        {
            lines.Add(("pam_mode", PamMode(server), "zPAM’s match format: " + HostingCatalog.MatchModeTitle(server.MatchMode)));
            lines.Add(("sv_wwwDownload", "1", "Players’ games fetch zPAM and its maps over the web"));
            lines.Add(("sv_wwwBaseURL", "http://cod2x.me/zpam", "Where they fetch them from; zPAM’s own default"));
        }
        else
        {
            lines.Add(("scr_friendlyfire", server.FriendlyFire ? "1" : "0", "Friendly fire: 1 on, 0 off"));
            lines.Add(("sv_pure", "1", "Players must have the same game files as the server"));
            lines.Add(("sv_allowDownload", "0", "The server doesn’t send files to players"));
        }
        string maps = string.Join(' ', Maps(server).Select(map => server.Rules == HostedRules.Zpam ? $"map {map}" : $"gametype {type} map {map}"));
        lines.Add(("sv_maprotation", maps, "Maps, in order"));
        lines.Add(("g_log", "games_mp.log", "The game’s own log of joins, kills and rounds"));
        return lines;
    }

    private static bool Word(string name) => name.Length > 0 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
}
