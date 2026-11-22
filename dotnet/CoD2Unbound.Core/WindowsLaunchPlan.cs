using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoD2Unbound.Core;

public enum WindowsRenderMode { Direct3D9, DirectX7 }

public sealed record WindowsLaunchPlan(EngineProcessStart Start, bool PlaysIntroFilms)
{
    private const string DedicatedPrefix = "+set dedicated 2 ";

    public static WindowsLaunchPlan Client(ClientLaunchRequest request, GameBinary binary, string workingDirectory,
        WindowsRenderMode renderMode = WindowsRenderMode.Direct3D9, IGameFiles? files = null, Guid? connectionId = null)
    {
        if (binary.Edition != request.Edition || binary.Role != EngineRole.Client)
            throw new ArgumentException("The binary must match the requested client edition.", nameof(binary));

        string renderer = renderMode switch
        {
            WindowsRenderMode.Direct3D9 => "dx9",
            WindowsRenderMode.DirectX7 => "dx7",
            _ => throw new ArgumentOutOfRangeException(nameof(renderMode))
        };
        var paths = request with { DataPath = EnginePath(request.DataPath), HomePath = EnginePath(request.HomePath) };
        var plan = GameLaunchPlan.BuildClient(paths, files, renderer, request.HomePath);
        ImmutableArray<string> arguments = plan.Arguments;
        ImmutableArray<PrivateLaunchFile> privateFiles = [];
        if (plan.Link is { } link)
        {
            if (request.Demo is not null)
                throw KeyedFailure("launch.conflictingActions");

            // WinMain logs its command line. A private config uses the engine's validated URL commands
            // without adding the password to process arguments, crash metadata or the environment.
            string name = $"join_{(connectionId ?? Guid.NewGuid()):N}.cfg";
            string connect = $"connect {link.Address}\n";
            string commands = link.HasPassword ? $"password \"{link.Password}\"\n" + connect : connect;
            privateFiles = [new("main/" + name, [.. Encoding.ASCII.GetBytes(commands)])];
            arguments = [.. arguments, "+exec", name];
        }

        ValidateTail(arguments, request.Edition, dedicated: false);
        var start = new EngineProcessStart(binary.Path, workingDirectory, request.HomePath, arguments, privateFiles)
        {
            EnvironmentChanges = CrashConsent(request.Edition == GameEdition.Cod2x && request.Settings.SendCoD2xReports)
        };
        return new(start, plan.PlaysIntroFilms);
    }

    public static WindowsLaunchPlan Dedicated(GameBinary binary, string workingDirectory, string dataPath, string homePath,
        int port, bool listed, string bind = "0.0.0.0")
    {
        if (binary.Role != EngineRole.Dedicated)
            throw new ArgumentException("The binary must be a dedicated server.", nameof(binary));
        if (port is < 1 or > 65535 || !IPAddress.TryParse(bind, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw KeyedFailure("hosting.invalidEndpoint");
        var arguments = GameLaunchPlan.DedicatedArguments(EnginePath(dataPath), EnginePath(homePath), port, listed, ip.ToString());
        ValidateTail(arguments, binary.Edition, dedicated: true);
        var start = new EngineProcessStart(binary.Path, workingDirectory, homePath, arguments, [])
        {
            EnvironmentChanges = CrashConsent(enabled: false).Add("COD2_CAPTURE_SERVER_CONSOLE", "1")
        };
        return new(start, false);
    }

    private static ImmutableDictionary<string, string?> CrashConsent(bool enabled) =>
        ImmutableDictionary<string, string?>.Empty.Add(CrashReporting.ConsentVariable, enabled ? "1" : null);

    private static LaunchPlanException KeyedFailure(string key) => new(key, key);

    private static string EnginePath(string path)
    {
        if (!GameSettings.SafeEngineText(path))
            throw new LaunchPlanException("Game paths contain unsupported characters.");

        // Forward slashes keep a trailing Windows separator from escaping the engine's closing quote.
        string normalized = path.Replace('\\', '/');
        bool drive = normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
        bool unc = false;
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            string[] parts = normalized[2..].Split('/', 3);
            unc = parts.Length >= 2 && parts[0].Length > 0 && parts[0] is not ("?" or ".") && parts[1].Length > 0;
        }
        if (!drive && !unc)
            throw KeyedFailure("launch.absolutePathRequired");
        return normalized;
    }

    private static void ValidateTail(ImmutableArray<string> arguments, GameEdition edition, bool dedicated)
    {
        string tail = WindowsCommandLine.ArgumentTail(arguments);
        if (dedicated)
            tail = DedicatedPrefix + tail;
        var limits = EngineCommandLine.Limits(edition);
        if (dedicated)
            limits = limits with { Bytes = Math.Min(limits.Bytes, 2047) };

        int commands = edition == GameEdition.Cod2x ? CountCod2xCommands(tail.AsSpan(), limits.Commands) :
            tail.Count(character => character is '+' or '\n');
        if (EngineCommandLine.Problem(commands, Encoding.UTF8.GetByteCount(tail), limits, edition, false, CultureInfo.CurrentCulture) is { } problem)
            throw new LaunchPlanException(problem);
    }

    private static int CountCod2xCommands(ReadOnlySpan<char> tail, int maxCommands)
    {
        if (tail.Length >= 2 && tail[0] == '"' && tail[^1] == '"')
            tail = tail[1..^1];
        bool quoted = false;
        int commands = 0;
        for (int index = 0; index < tail.Length; index++)
        {
            char current = tail[index];
            char next = index + 1 < tail.Length ? tail[index + 1] : '\0';
            if (quoted && current == '\\' && next == '"')
            {
                index++;
                continue;
            }
            if (quoted && current == ' ' && next == '+')
                quoted = false;
            else if (current == '"')
                quoted = !quoted;
            else if (quoted)
                continue;
            if (current is '+' or '\n' or ',')
            {
                if (commands == maxCommands)
                    return commands + 1;
                quoted = false;
                if (next is not ('\0' or '+' or '\n' or ','))
                    commands++;
            }
        }
        return commands;
    }
}

public static class WindowsCommandLine
{
    // Pass the executable separately as lpApplicationName. MinGW WinMain receives the raw tail.
    // ProcessStartInfo.ArgumentList performs CRT argv escaping, which would alter the engine's quotes.
    public static string Format(EngineProcessStart process)
    {
        if (string.IsNullOrWhiteSpace(process.Executable) || process.Executable.AsSpan().ContainsAny('"', '\0', '\r'))
            throw new ArgumentException("Invalid executable path.", nameof(process));
        string tail = ArgumentTail(process.EngineArguments);
        return tail.Length == 0 ? GameLaunchPlan.Quote(process.Executable) : GameLaunchPlan.Quote(process.Executable) + " " + tail;
    }

    internal static string ArgumentTail(ImmutableArray<string> arguments)
    {
        if (arguments.Any(argument => argument.AsSpan().ContainsAny('\0', '\r')))
            throw new LaunchPlanException("Dvar values cannot contain quotes, separators or control characters.");
        return string.Join(' ', arguments);
    }
}
