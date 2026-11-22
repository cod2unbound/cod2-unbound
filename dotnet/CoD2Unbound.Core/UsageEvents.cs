using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum UsageConsent { NotAsked, No, Yes }
public enum UsageSource { Setup, Settings }
public enum UsagePage
{
    Home, Servers, Library, Configs, Hosting, Setups, Saved, PlayerSetup, Checker, Loadout, Binds,
    Checkup, History, General, Display, Data, Privacy, Updates, Advanced, About
}
public enum UsageJoin { List, Favorite, Link, TypedAddress }
public enum UsageMode { Unknown, Borderless, Exclusive, Windowed }
public enum UsageProblem
{
    GameStart, GameExit, GameData, Address, ServerLink, ServerList, Join, ServerVersion, Hosting, Directory,
    Import, Check, SaveLoadout, UpdateCheck, UpdateInstall, SaveSettings, SaveSetup
}
public enum UsageSetting
{
    HideWhilePlaying, RefreshServers, VerticalSync, RawMouse, CrashReports, Cod2xReports, MatchSummaries, AutoUpdates,
    GameVersion, Appearance, Mode, Graphics, TextureFiltering, Resolution, Sensitivity, Dpi, Volume, ExtraOptions,
    ResetWarnings, GameDataFolder, CdKey
}
public enum UsageKind
{
    AppOpened, ScreenOpened, SetupReached, SetupSkipped, GameStarted, GameEnded, ServerListRefreshed, ServerJoined,
    SetupViewed, SetupTried, SetupImported, LoadoutMade, CheckRun, HostingStarted, UpdateOffered, UpdateInstalled,
    UpdateDownloading, UpdateVerifying, UpdateReady, UpdateInstalling, SettingChanged, ErrorShown, TurnedOn
}

/// The journal stores typed choices. The encoder checks them again after a restart.
public sealed record UsageEvent(UsageKind Kind, int Choice = 0, int Other = 0, int Number = 0,
    string? First = null, string? Second = null)
{
    [JsonInclude]
    internal string? CatalogueSlug { get; init; }

    public static UsageEvent Page(UsagePage page) => new(UsageKind.ScreenOpened, (int)page);
    public static UsageEvent Setup(SetupStep step, bool skipped = false, bool ownFiles = false) =>
        new(skipped ? UsageKind.SetupSkipped : UsageKind.SetupReached, (int)step, ownFiles ? 1 : 0);
    public static UsageEvent Start(GameEdition game, WindowsRenderMode renderer, UsageMode mode) =>
        new(UsageKind.GameStarted, (int)game, (int)renderer, (int)mode);
    public static UsageEvent End(bool crashed, int minutes) => new(UsageKind.GameEnded, crashed ? 1 : 0, Number: minutes);
    public static UsageEvent Join(UsageJoin source, GameEdition game) => new(UsageKind.ServerJoined, (int)source, (int)game);
    public static UsageEvent Problem(UsageProblem kind) => new(UsageKind.ErrorShown, (int)kind);
    public static UsageEvent Update(UsageKind kind, string from, string to) => new(kind, First: Version(from), Second: Version(to));
    public static UsageEvent? Catalogue(UsageKind kind, ConfigDirectorySnapshot snapshot, string candidate) =>
        kind is UsageKind.SetupViewed or UsageKind.SetupTried or UsageKind.SetupImported &&
        snapshot.Index.Entries.Any(entry => entry.Id == candidate) && SafeSlug(candidate)
            ? new(kind) { CatalogueSlug = candidate } : null;

    internal bool IsSetup => Kind is UsageKind.SetupReached or UsageKind.SetupSkipped;

    private static readonly string[] Names =
    [
        "app opened", "screen opened", "setup step reached", "setup step skipped", "game started", "game ended",
        "server list refreshed", "server joined", "player setup viewed", "player setup tried", "player setup imported",
        "loadout made", "check run", "hosting started", "update offered", "update installed", "update downloading",
        "update verifying", "update ready", "update installing", "setting changed", "error shown", "usage data turned on"
    ];
    private static readonly string[] Steps = ["Welcome", "Game data", "CD key", "Make it yours", "Crash reports", "Ready", "", "Usage data"];
    private static readonly string[] Pages =
    [
        "Home", "Servers", "Library", "Configs", "Servers › Hosting", "Configs › Player Setups", "Configs › Saved",
        "Configs › Player Setup", "Configs › Check a Config", "Configs › Loadout settings", "Configs › Binds",
        "Configs › Check-up", "Configs › History", "Settings › General", "Settings › Display", "Settings › Game Data",
        "Settings › Privacy", "Settings › Updates", "Settings › Advanced", "Settings › About"
    ];
    private static readonly string[] Problems =
    [
        "the game couldn’t start", "the game closed unexpectedly", "game data could not be used", "invalid server address",
        "server link could not be delivered", "the server list could not be refreshed", "the server could not be joined",
        "the server’s game version is unavailable", "a hosted server could not run", "the player catalogue could not be loaded",
        "a config could not be imported", "a config could not be checked", "a loadout could not be saved", "update check failed",
        "update installation failed", "settings could not be saved", "setup could not be saved"
    ];
    private static readonly string[] Settings =
    [
        "Hide CoD2 Unbound while the game is running", "Refresh the server list when Servers opens", "Vertical sync",
        "Raw mouse input", "Send crash reports to CoD2 Unbound", "Send crashes to CoD2x", "Match summaries",
        "Check for updates automatically", "Game version", "Appearance", "Mode", "Graphics", "Texture sharpness at a distance",
        "Resolution", "Sensitivity", "DPI", "Game volume", "Extra launch options", "Reset Warnings", "Game data folder", "CD key"
    ];

    internal string? AddProperties(SortedDictionary<string, object?> fields)
    {
        var name = Item(Names, (int)Kind);
        switch (Kind)
        {
            case UsageKind.ScreenOpened: if (!Put("screen", Item(Pages, Choice))) return null; break;
            case UsageKind.SetupReached:
            case UsageKind.SetupSkipped:
                if (!Put("step", Item(Steps, Choice))) return null;
                if (Kind == UsageKind.SetupReached && Choice == (int)SetupStep.Data && Other == 1)
                    fields["source"] = "own files";
                break;
            case UsageKind.GameStarted:
                if (!Put("game", Edition(Choice)) || !Put("renderer", Renderer(Other))) return null;
                fields["display_mode"] = Mode(Number);
                fields["render_scale"] = 100;
                break;
            case UsageKind.GameEnded:
                fields["how"] = Choice == 1 ? "crashed" : "closed normally";
                fields["minutes"] = Math.Clamp(Number, 0, 7 * 24 * 60);
                break;
            case UsageKind.ServerListRefreshed:
                if (!Put("list", Item(["all", "favorites", "recent"], Choice))) return null;
                break;
            case UsageKind.ServerJoined:
                if (!Put("how", Item(["from the list", "from a favourite", "from a link", "from a typed address"], Choice))) return null;
                if (Edition(Other) is { } game) fields["game"] = game;
                break;
            case UsageKind.SetupViewed:
            case UsageKind.SetupTried:
            case UsageKind.SetupImported:
                if (CatalogueSlug is null || !SafeSlug(CatalogueSlug)) return null;
                fields["setup_id"] = CatalogueSlug;
                break;
            case UsageKind.HostingStarted:
                if (!Put("game", Edition(Other))) return null;
                fields["reach"] = Choice == 1 ? "the internet" : "my network";
                break;
            case UsageKind.UpdateOffered:
            case UsageKind.UpdateInstalled:
            case UsageKind.UpdateDownloading:
            case UsageKind.UpdateVerifying:
            case UsageKind.UpdateReady:
            case UsageKind.UpdateInstalling:
                if (!Put("from", Version(First)) || !Put("to", Version(Second))) return null;
                break;
            case UsageKind.SettingChanged:
                if (!Put("setting", Item(Settings, Choice))) return null;
                object? value = (UsageSetting)Choice switch
                {
                    <= UsageSetting.AutoUpdates when Other is 0 or 1 => Other == 1,
                    UsageSetting.GameVersion => Edition(Other),
                    UsageSetting.Appearance => Item(["system", "light", "dark"], Other),
                    UsageSetting.Mode when Other is >= 1 and <= 3 => Mode(Other),
                    UsageSetting.Graphics => Renderer(Other),
                    UsageSetting.TextureFiltering when Other is 2 or 4 or 8 or 16 => Other,
                    _ => null
                };
                if (value is not null) fields["value"] = value;
                break;
            case UsageKind.ErrorShown: if (!Put("kind", Item(Problems, Choice))) return null; break;
            case UsageKind.TurnedOn:
                if (!Put("where", Item(["setup", "settings"], Choice))) return null;
                break;
        }
        return name;

        bool Put(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            fields[key] = value;
            return true;
        }
    }

    public static IEnumerable<UsageEvent> Changed(GameSettings old, GameSettings next)
    {
        var result = new List<UsageEvent>();
        Add(UsageSetting.HideWhilePlaying, old.HideWhilePlaying, next.HideWhilePlaying, next.HideWhilePlaying ? 1 : 0);
        Add(UsageSetting.RefreshServers, old.RefreshServersOnOpen, next.RefreshServersOnOpen, next.RefreshServersOnOpen ? 1 : 0);
        Add(UsageSetting.VerticalSync, old.Vsync, next.Vsync, next.Vsync ? 1 : 0);
        Add(UsageSetting.RawMouse, old.RawMouse, next.RawMouse, next.RawMouse ? 1 : 0);
        Add(UsageSetting.CrashReports, old.SendCrashReports, next.SendCrashReports, next.SendCrashReports ? 1 : 0);
        Add(UsageSetting.Cod2xReports, old.SendCoD2xReports, next.SendCoD2xReports, next.SendCoD2xReports ? 1 : 0);
        Add(UsageSetting.MatchSummaries, old.MatchSummariesEnabled, next.MatchSummariesEnabled, next.MatchSummariesEnabled ? 1 : 0);
        Add(UsageSetting.AutoUpdates, old.CheckUpdatesAutomatically, next.CheckUpdatesAutomatically, next.CheckUpdatesAutomatically ? 1 : 0);
        Add(UsageSetting.GameVersion, old.Engine, next.Engine, (int)next.Engine);
        Add(UsageSetting.Appearance, old.Appearance, next.Appearance, (int)next.Appearance);
        Add(UsageSetting.Mode, old.Fullscreen, next.Fullscreen, (int)ModeFor(next.Fullscreen));
        Add(UsageSetting.Graphics, old.WindowsRenderer, next.WindowsRenderer, (int)(next.WindowsRenderer ?? WindowsRenderMode.Direct3D9));
        Add(UsageSetting.TextureFiltering, old.Anisotropy, next.Anisotropy, next.Anisotropy is 2 or 4 or 8 or 16 ? (int)next.Anisotropy : 0);
        Add(UsageSetting.Resolution, old.Resolution, next.Resolution);
        Add(UsageSetting.Sensitivity, old.Sensitivity, next.Sensitivity);
        Add(UsageSetting.Dpi, old.Dpi, next.Dpi);
        Add(UsageSetting.Volume, old.Volume, next.Volume);
        Add(UsageSetting.ExtraOptions, old.Advanced, next.Advanced);
        if (!old.DismissedWarnings.IsEmpty && next.DismissedWarnings.IsEmpty)
            result.Add(new(UsageKind.SettingChanged, (int)UsageSetting.ResetWarnings));
        return result;

        void Add<T>(UsageSetting setting, T before, T after, int choice = 0)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after))
                result.Add(new(UsageKind.SettingChanged, (int)setting, choice));
        }
    }

    public static UsageMode ModeFor(string value) => value switch
    {
        "borderless" => UsageMode.Borderless, "exclusive" => UsageMode.Exclusive, "windowed" => UsageMode.Windowed, _ => UsageMode.Unknown
    };
    private static string Mode(int value) => Item(["unknown", "borderless", "exclusive", "windowed"], value) ?? "unknown";
    private static string? Renderer(int value) => (WindowsRenderMode)value switch
    {
        WindowsRenderMode.Direct3D9 => "Direct3D 9", WindowsRenderMode.DirectX7 => "DirectX 7", _ => null
    };
    private static string? Edition(int value) => (GameEdition)value switch
    {
        GameEdition.Cod2x => "CoD2x", GameEdition.Original => "Original 1.3", _ => null
    };
    private static string? Item(string[] values, int value) => (uint)value < values.Length ? values[value] : null;
    private static bool SafeSlug(string value) => value.Length <= 64 &&
        Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant) && !CrashReporting.ContainsKey(value);

    public static string? Version(string? value)
    {
        if (value is null || value.Length > 18)
            return null;
        var parts = (value.StartsWith('v') ? value[1..] : value).Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length is < 1 or > 5 || !part.All(char.IsAsciiDigit) ||
            !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;
        return string.Join('.', parts.Select(part => ushort.Parse(part, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }
}

public sealed record UsageFacts(string App, string Windows, string PcModel, string Processor, int DisplayWidth,
    int DisplayHeight, int DisplayHz, string Language)
{
    public UsageHardware? Hardware { get; init; }

    private const string Unknown = "unknown";
    private const string ModelUnavailable = "no model given";
    private const string Manufacturer = @"(?:Acer(?: Inc\.)?|Alienware|Apple(?: Inc\.)?|ASUS(?:TEK COMPUTER INC\.)?|ASRock|Dell(?: Inc\.)?|Fujitsu|Framework|Gigabyte(?: Technology Co\., Ltd\.)?|Google|HP|Hewlett[- ]Packard|Huawei|Intel(?: Corporation)?|Lenovo|LG|MEDION|Micro-Star(?: International Co\., Ltd\.)?|Microsoft(?: Corporation)?|MINISFORUM|MSI|Panasonic|Parallels|QEMU|Razer|Samsung|Sony(?: Corporation)?|Toshiba|VMware(?:, Inc\.)?)";
    private static readonly HashSet<string> NoModel = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "System Product Name", "To Be Filled By O.E.M.", "To Be Filled By OEM", "Default string", "System Version",
        "System Manufacturer", "System Name", "Not Applicable", "Not Specified", "Not Available", "OEM", "None", "Unknown", "N/A",
        "All Series", "Type1ProductConfigId", "Product Name", "SKU", "Base Board Product Name", "QEMU Virtual Machine"
    };

    public static string Model(string? maker, string? model, params string[] privateValues)
    {
        string product = model?.Trim() ?? "";
        string manufacturer = maker?.Trim() ?? "";
        if (NoModel.Contains(product) || !Regex.IsMatch(manufacturer, @"\A" + Manufacturer + @"\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            return ModelUnavailable;
        return SafeModel(manufacturer + " " + product, privateValues);
    }

    public static string SafeModel(string? value, params string[] privateValues) =>
        Text(value, @"\A" + Manufacturer + @" (?:MS-[A-Z0-9]{4}|[A-Z0-9]{2,6}|[A-Z0-9][A-Z0-9().-]* [A-Z0-9][A-Z0-9 ().-]*)\z",
            ModelUnavailable, privateValues);

    public static string SafeProcessor(string? value, params string[] privateValues) =>
        Text(value?.Trim(), @"\A(?:(?:[0-9]{1,2}(?:st|nd|rd|th) Gen )?Intel(?:\(R\))? (?:Core|Xeon|Pentium|Celeron|Processor|Atom)|AMD (?:Ryzen|EPYC|Athlon|Phenom|FX|A[0-9]+)|Qualcomm(?:\(R\))? (?:Snapdragon|Oryon)|Snapdragon|Apple M[0-9])[A-Z0-9 ().+@-]*\z",
            Unknown, privateValues);

    public static string SafeWindows(string? value) => value is { Length: <= 21 } &&
        Regex.IsMatch(value, @"\A[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,8}\z", RegexOptions.CultureInvariant) ? value : Unknown;

    public static string SafeGraphics(string? value, params string[] privateValues) =>
        Text(value?.Trim(' '), GraphicsPattern, Unknown, privateValues, requireDigit: false);

    private const string GraphicsPattern = """
        (?x)\A(?:
          NVIDIA(?:\(R\))?\ (?:GeForce\ (?:RTX|GTX|GTS|GT|MX)\ ?[0-9]{2,4}(?:M|\ Ti|\ SUPER|\ Ti\ SUPER)?(?:\ (?:Laptop\ )?GPU)?|
            (?:Quadro\ (?:RTX\ )?|Tesla\ |RTX\ (?:A|PRO\ )?)[A-Z]?[0-9]{2,4}(?:\ (?:Ada\ Generation|Blackwell(?:\ (?:Workstation|Max-Q|Server)\ Edition)?|Laptop\ GPU))?)|
          AMD\ Radeon(?:\(TM\))?(?:\ (?:RX|PRO|HD|R[579]|Vega)\ [A-Z]?[0-9]{1,4}(?:M|M?\ (?:XT|XTX|GRE|S))?|(?:\ [0-9]{3}M)?\ Graphics)|
          Intel(?:\(R\))?\ (?:Arc(?:\(TM\))?(?:\ (?:Pro\ )?[AB][0-9]{2,3}M?)?|UHD|HD|Iris(?:\(R\))?\ (?:Xe(?:\ MAX)?|Plus|Pro))\ Graphics(?:\ [0-9]{3,4})?|
          Qualcomm(?:\(R\))?\ Adreno(?:\(TM\))?\ X[0-9]-[0-9]{2}(?:\ GPU)?|
          Microsoft\ Basic\ (?:Render\ Driver|Display\ Adapter)|
          Parallels\ Display\ Adapter\ \(WDDM\)|
          VMware\ SVGA\ 3D
        )\z
        """;

    private static string Text(string? value, string pattern, string fallback, string[] privateValues, bool requireDigit = true) =>
        value is { Length: <= 64 } && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) &&
        (!requireDigit || value.Any(char.IsAsciiDigit)) && !CrashReporting.ContainsKey(value) &&
        !Regex.IsMatch(value, @"[a-f0-9]{12,}|[0-9]{9,}|(?:[a-f0-9]{2}-){5}[a-f0-9]{2}|(?:^| )S-[0-9]-|(?:[0-9]{1,3}\.){3}[0-9]{1,3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !privateValues.Any(secret => !string.IsNullOrWhiteSpace(secret) &&
            Regex.IsMatch(value, @"(?<![A-Za-z0-9])" + Regex.Escape(secret) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            ? value : fallback;

    internal SortedDictionary<string, object?> Properties()
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$lib"] = "cod2unbound-launcher",
            ["$process_person_profile"] = false,
            ["app"] = UsageEvent.Version(App) ?? Unknown,
            ["windows"] = SafeWindows(Windows),
            ["pc_model"] = SafeModel(PcModel),
            ["processor"] = SafeProcessor(Processor),
            ["display_width"] = Math.Clamp(DisplayWidth, 0, 32768),
            ["display_height"] = Math.Clamp(DisplayHeight, 0, 32768),
            ["display_hz"] = Math.Clamp(DisplayHz, 0, 1000),
            ["language"] = Language is { Length: 2 or 3 } && Language.All(c => c is >= 'a' and <= 'z') ? Language : Unknown
        };
        if (Hardware is { } hardware)
        {
            fields.Add("processor_cores", Math.Clamp(hardware.ProcessorCores, 1, 1024));
            fields.Add("pc_arch", UsageHardware.SafeArchitecture(hardware.PcArchitecture));
            fields.Add("app_arch", UsageHardware.SafeArchitecture(hardware.AppArchitecture));
            fields.Add("memory_gb", Math.Clamp(hardware.MemoryGb, 1, 4096));
            fields.Add("graphics", SafeGraphics(hardware.Graphics));
            fields.Add("graphics_memory_gb", Math.Clamp(hardware.GraphicsMemoryGb, 0, (long)(ulong.MaxValue >> 30)));
            fields.Add("graphics_driver", UsageHardware.SafeGraphicsDriver(hardware.GraphicsDriver));
        }
        return fields;
    }
}

public sealed record UsageHardware(int ProcessorCores, string? PcArchitecture, string? AppArchitecture, int MemoryGb,
    string? Graphics, long GraphicsMemoryGb, string? GraphicsDriver)
{
    public static int InstalledMemoryGb(ulong kibibytes)
    {
        const ulong kibPerGb = 1024 * 1024;
        ulong rounded = kibibytes / kibPerGb + (kibibytes % kibPerGb >= kibPerGb / 2 ? 1UL : 0UL);
        return (int)Math.Clamp(rounded, 1UL, 4096UL);
    }

    internal static string? SafeArchitecture(string? value) => value is "x64" or "arm64" ? value : null;

    internal static string? SafeGraphicsDriver(string? value) =>
        value is { Length: >= 7 and <= 23 } &&
        Regex.IsMatch(value, @"\A[0-9]{1,5}(?:\.[0-9]{1,5}){3}\z", RegexOptions.CultureInvariant) &&
        value.Split('.').All(part => ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? value : null;
}
