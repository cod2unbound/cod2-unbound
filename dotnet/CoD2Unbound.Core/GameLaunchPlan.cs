using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public sealed class LaunchPlanException(string message, string? messageKey = null) : Exception(message)
{
    public string? MessageKey { get; } = messageKey;
}

public readonly record struct EngineLimits(int Commands, int Bytes);

public static class EngineCommandLine
{
    public static EngineLimits Limits(GameEdition edition) => edition switch
    {
        GameEdition.Cod2x => new(32, 1023),
        // Original 1.3 reserves the first of its 32 slots for text before the first '+'.
        GameEdition.Original => new(31, 3899),
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    public static string? Problem(IEnumerable<string> arguments, GameEdition edition, bool inEditor = false,
        CultureInfo? culture = null)
    {
        string text = string.Join(' ', arguments);
        return Problem(text.Count(character => character == '+'), Encoding.UTF8.GetByteCount(text), Limits(edition), edition, inEditor,
            culture ?? CultureInfo.CurrentCulture);
    }

    internal static string? Problem(int commands, int bytes, EngineLimits limits, GameEdition edition, bool inEditor, CultureInfo culture)
    {
        string label = edition == GameEdition.Cod2x ? "CoD2x" : "Original 1.3";
        if (commands > limits.Commands)
        {
            int extra = commands - limits.Commands;
            return $"This start needs {commands} startup commands and {label} takes {limits.Commands}. Remove at least " +
                (extra == 1 ? "one line" : $"{extra} lines") + (inEditor ? "." : " from Extra launch options in Settings › Advanced.");
        }
        return bytes > limits.Bytes
            ? $"This start’s command line is {bytes.ToString("N0", culture)} characters and {label} takes {limits.Bytes.ToString("N0", culture)}. Shorten or remove lines" +
              (inEditor ? "." : " in Extra launch options in Settings › Advanced.")
            : null;
    }
}

public sealed partial record ConfigStudioLaunch(string ProfileId, string? Chain = null, bool ConnectionIsolated = false)
{
    public static string ChainBasename(string profileId)
    {
        ValidateProfileId(profileId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(profileId.ToLowerInvariant()));
        return "cs_" + Convert.ToHexStringLower(hash.AsSpan(0, 20)) + ".cfg";
    }

    internal static void ValidateProfileId(string profileId)
    {
        if (!IsProfileId(profileId))
            throw new LaunchPlanException("The loadout identifier is invalid.");
    }

    internal static bool IsProfileId(string profileId) => ProfileIdPattern().IsMatch(profileId);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();
}

public sealed partial record ConfigConnectionLaunch(string Address, ImmutableDictionary<string, string> Values)
{
    internal static readonly ImmutableHashSet<string> AllowedSettings = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "com_maxfps", "rate", "snaps", "cl_maxpackets", "m_filter", "cl_mouseaccel", "r_picmip", "r_picmip_manual");

    internal IEnumerable<string> ArgumentsFor(LaunchLink? link)
    {
        if (link is null || LaunchLink.CanonicalAddress(link.Address) != LaunchLink.CanonicalAddress(Address) ||
            Values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Values.Count)
            throw new LaunchPlanException("Temporary settings belong to a different connection.");

        foreach (var (name, value) in Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!AllowedSettings.Contains(name) || !NumericDvar().IsMatch(value) || !GameSettings.SafeValue(value) ||
                !double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
                throw new LaunchPlanException("This temporary setting is not an editable, numeric client option.");
            yield return "+set";
            yield return name.ToLowerInvariant();
            yield return GameLaunchPlan.Quote(value);
        }
    }

    [GeneratedRegex(@"\A[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDvar();
}

public sealed record ClientLaunchRequest(GameEdition Edition, GameSettings Settings, string DataPath, string HomePath)
{
    public LaunchLink? Link { get; init; }
    public string? Demo { get; init; }
    public ImmutableArray<string> Forwarded { get; init; } = [];
    public ConfigStudioLaunch? Studio { get; init; }
    public ConfigConnectionLaunch? Connection { get; init; }
    public ImmutableDictionary<string, string>? Loadout { get; init; }
}

public sealed partial record GameLaunchPlan(ImmutableArray<string> Arguments, LaunchLink? Link, bool PlaysIntroFilms)
{
    public static GameLaunchPlan Client(ClientLaunchRequest request, IGameFiles? files = null)
    {
        var plan = BuildClient(request, files);
        if (EngineCommandLine.Problem(plan.Arguments, request.Edition) is { } problem)
            throw new LaunchPlanException(problem);
        return plan;
    }

    internal static GameLaunchPlan BuildClient(ClientLaunchRequest request, IGameFiles? files,
        string? windowsRenderer = null, string? originalHome = null)
    {
        if (!GameSettings.SafeValue(request.DataPath) || !GameSettings.SafeValue(request.HomePath))
            throw new LaunchPlanException("Game paths contain unsupported characters.");

        bool intro = !request.Settings.IntroFilmsPlayed && request.Link is null && request.Demo is null && request.Forwarded.IsEmpty;
        bool migrated = request.Studio is not null;
        var arguments = ImmutableArray.CreateBuilder<string>();
        foreach (var (name, value) in request.Settings.LaunchDvars(migrated))
            AddVariable(arguments, "+set", name, name.Equals("com_introplayed", StringComparison.OrdinalIgnoreCase) ? intro ? "0" : "1" : value);

        if (request.Loadout is { } loadout)
        {
            var values = loadout.WithComparers(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in request.Settings.LoadoutDefaults(migrated, values))
                if (windowsRenderer is null || !name.Equals("r_rendererPreference", StringComparison.OrdinalIgnoreCase))
                    AddVariable(arguments, "+seta", name, value);
        }

        AddVariable(arguments, "+set", "fs_basepath", request.DataPath);
        AddVariable(arguments, "+set", "fs_homepath", request.HomePath);

        if (request.Studio is { } studio)
        {
            ConfigStudioLaunch.ValidateProfileId(studio.ProfileId);
            if (studio.Chain is { } chain)
            {
                if (chain != ConfigStudioLaunch.ChainBasename(studio.ProfileId))
                    throw new LaunchPlanException("The loadout's advanced chain filename is invalid.");
                if (files?.IsRegularFile(originalHome ?? request.HomePath, "main/" + chain) == true)
                    arguments.AddRange(new[] { "+exec", chain });
            }
        }

        if (request.Demo is { } demo)
        {
            if (!MediaName.Playable(demo))
                throw new LaunchPlanException("Rename this demo using letters, numbers, dots, underscores or hyphens before playback.");
            arguments.AddRange(new[] { "+demo", demo });
        }

        if (request.Connection is { } connection)
        {
            if (!migrated || request.Demo is not null)
                throw new LaunchPlanException("Temporary server settings require a saved loadout and a server connection.");
            if (!connection.Values.IsEmpty && request.Studio?.ConnectionIsolated != true)
                throw new LaunchPlanException("Temporary server settings require an isolated connection write owner.");
        }

        arguments.AddRange(request.Forwarded);
        if (windowsRenderer is not null)
            AddVariable(arguments, "+set", "r_rendererPreference", windowsRenderer);
        if (request.Connection is not null)
            arguments.AddRange(request.Connection.ArgumentsFor(request.Link));

        return new(arguments.ToImmutable(), request.Link, intro);
    }

    public static GameLaunchPlan Dedicated(GameEdition edition, string dataPath, string homePath, int port, bool listed,
        string bind = "0.0.0.0")
    {
        if (!dataPath.StartsWith('/') || !homePath.StartsWith('/') ||
            dataPath.AsSpan().ContainsAny("\"+;\n\r") || homePath.AsSpan().ContainsAny("\"+;\n\r"))
            throw new LaunchPlanException("The game or server folder has a quote, plus sign or semicolon in its path, which the game can’t read. Move it and try again.");
        var arguments = DedicatedArguments(dataPath, homePath, port, listed, bind);
        if (EngineCommandLine.Problem(arguments, edition) is { } problem)
            throw new LaunchPlanException(problem);
        return new(arguments, null, false);
    }

    internal static ImmutableArray<string> DedicatedArguments(string dataPath, string homePath, int port, bool listed, string bind) =>
        ["+set", "fs_basepath", Quote(dataPath), "+set", "fs_homepath", Quote(homePath), "+set", "net_ip", bind, "+set",
         "net_port", port.ToString(CultureInfo.InvariantCulture), "+set", "dedicated", listed ? "2" : "1", "+exec", "server.cfg"];

    internal static string Quote(string value) => "\"" + value + "\"";

    private static void AddVariable(ImmutableArray<string>.Builder arguments, string command, string name, string value)
    {
        arguments.Add(command);
        arguments.Add(name);
        arguments.Add(Quote(value));
    }

}
