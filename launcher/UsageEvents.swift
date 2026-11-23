import AppKit
import CoreGraphics
import Foundation

enum UsageConsentSource: String, Codable, Sendable { case setup, sheet, settings }
enum UsageSetupStep: String, Codable, Sendable {
    case welcome = "Welcome", gameData = "Game data", cdKey = "CD key", shaders = "Shaders"
    case yours = "Make it yours", crashReports = "Crash reports", usageData = "Usage data", ready = "Ready"
}
enum UsageDataSource: String, Codable, Sendable { case steam = "Steam", ownFiles = "own files" }
enum UsageDisplayMode: String, Codable, Sendable { case borderless, exclusive, spaces, windowed, unknown }
enum UsageGameEnd: String, Codable, Sendable {
    case normal = "closed normally", crash = "crashed", freeze = "froze"

    static func observed(code: Int32?, requestedQuit: Bool, reports: [URL]) -> Self? {
        if reports.contains(where: { $0.lastPathComponent.hasPrefix("cod2_freeze_") || $0.lastPathComponent.hasPrefix("cod2x_freeze.") }) { return .freeze }
        if !reports.isEmpty { return .crash }
        if code == 0 || code == 1 || requestedQuit && code == SIGTERM { return .normal }
        // When reattached, the launcher cannot read a status from a process it did not start.
        return code.map { _ in .crash }
    }
}
enum UsageServerList: String, Codable, Sendable { case all, favorites, recent }

enum UsageScreen: String, Codable, Sendable {
    case home = "Home", servers = "Servers", library = "Library", configs = "Configs"
    case serversHosting = "Servers › Hosting"
    case configsSetups = "Configs › Player Setups", configsSaved = "Configs › Saved", configsPlayerSetup = "Configs › Player Setup"
    case configsChecker = "Configs › Check a Config", configsSettings = "Configs › Loadout settings"
    case configsBinds = "Configs › Binds", configsCheckup = "Configs › Check-up", configsHistory = "Configs › History"
    case settingsGeneral = "Settings › General", settingsDisplay = "Settings › Display", settingsData = "Settings › Game Data"
    case settingsPrivacy = "Settings › Privacy", settingsUpdates = "Settings › Updates"
    case settingsAdvanced = "Settings › Advanced", settingsAbout = "Settings › About"
}

enum UsageError: String, Codable, Sendable {
    case gameCouldNotStart = "the game couldn’t start", gameEndedUnexpectedly = "the game closed unexpectedly"
    case badGameData = "game data could not be used", shaderBuild = "shaders could not be prepared"
    case serverAddress = "invalid server address", serverLink = "server link could not be delivered"
    case serverList = "the server list could not be refreshed", serverJoin = "the server could not be joined"
    case serverVersion = "the server’s game version is unavailable", hosting = "a hosted server could not run"
    case directory = "the player catalogue could not be loaded", importConfig = "a config could not be imported"
    case checkConfig = "a config could not be checked", saveLoadout = "a loadout could not be saved"
    case share = "a setup could not be shared", withdraw = "sharing could not be stopped"
    case updateCheck = "update check failed", updateInstall = "update installation failed"
    case saveSettings = "settings could not be saved", saveSetup = "setup could not be saved"
    case steam = "Steam download failed"
}

/// Version strings come from the signed bundle or the project's release feed.
/// Restrict them to numeric release tags and encode numbers, never the input string.
struct UsageVersion: Codable, Sendable {
    private let major: UInt16
    private let minor: UInt16
    private let patch: UInt16

    init?(_ version: String) {
        let tag = version.hasPrefix("v") ? version.dropFirst() : version[...]
        let parts = tag.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts.allSatisfy({ $0.utf8.count <= 5 && $0.utf8.allSatisfy { (48...57).contains($0) } }),
              let major = UInt16(parts[0]), let minor = UInt16(parts[1]), let patch = UInt16(parts[2]) else { return nil }
        self.major = major; self.minor = minor; self.patch = patch
    }

    var label: String { "\(major).\(minor).\(patch)" }
}

/// Names are fixed here. Only switches and fixed choices have a value on the wire.
enum UsageSetting: Codable, Sendable {
    enum Switch: String, Codable, Sendable {
        case hideWhilePlaying = "Hide CoD2 Unbound while the game is running"
        case refreshServers = "Refresh the server list when Servers opens"
        case verticalSync = "Vertical sync", rawMouse = "Raw mouse input"
        case crashReports = "Send crash reports to CoD2 Unbound", cod2xReports = "Send crashes to CoD2x"
        case matchSummaries = "Match summaries", automaticUpdates = "Check for updates automatically"
    }

    case toggle(Switch, Bool)
    case engine(GameEngine), appearance(LauncherAppearance), displayMode(UsageDisplayMode), renderer(GameRenderer)
    case renderScale(Int), textureFiltering(Int), frameCap(Int?)
    case resolution, sensitivity, dpi, volume, extraLaunchOptions, resetWarnings, gameDataFolder, cdKey, cod2xMenus

    static func changes(from old: GameSettings, to new: GameSettings) -> [Self] {
        var result: [Self] = []
        func changed<T: Equatable>(_ key: KeyPath<GameSettings, T>, _ event: (T) -> Self) {
            if old[keyPath: key] != new[keyPath: key] { result.append(event(new[keyPath: key])) }
        }
        changed(\.hideWhilePlaying) { .toggle(.hideWhilePlaying, $0) }
        changed(\.refreshServersOnOpen) { .toggle(.refreshServers, $0) }
        changed(\.vsync) { .toggle(.verticalSync, $0) }
        changed(\.rawMouse) { .toggle(.rawMouse, $0) }
        changed(\.sendCrashReports) { .toggle(.crashReports, $0) }
        changed(\.sendCoD2xReports) { .toggle(.cod2xReports, $0) }
        changed(\.matchSummariesEnabled) { .toggle(.matchSummaries, $0) }
        changed(\.checkUpdatesAutomatically) { .toggle(.automaticUpdates, $0) }
        changed(\.engine, Self.engine)
        changed(\.appearance, Self.appearance)
        changed(\.fullscreen) { .displayMode(UsageDisplayMode(rawValue: $0) ?? .unknown) }
        changed(\.renderer, Self.renderer)
        changed(\.renderScale, Self.renderScale)
        changed(\.anisotropy, Self.textureFiltering)
        changed(\.fps) { .frameCap(FrameCap.isPreset($0) ? $0 : nil) }
        changed(\.resolution) { _ in .resolution }
        changed(\.sensitivity) { _ in .sensitivity }
        changed(\.dpi) { _ in .dpi }
        changed(\.volume) { _ in .volume }
        changed(\.advanced) { _ in .extraLaunchOptions }
        if !old.dismissedWarnings.isEmpty, new.dismissedWarnings.isEmpty { result.append(.resetWarnings) }
        return result
    }

    fileprivate var fields: [String: UsageProperty] {
        let name: String
        let value: UsageProperty?
        switch self {
        case .toggle(let setting, let enabled): (name, value) = (setting.rawValue, .flag(enabled))
        case .engine(let engine): (name, value) = ("Game version", .text(engine.versionName))
        case .appearance(let appearance): (name, value) = ("Appearance", .text(appearance.rawValue))
        case .displayMode(let mode): (name, value) = ("Mode", mode == .unknown ? nil : .text(mode.rawValue))
        case .renderer(let renderer): (name, value) = ("Graphics", .text(renderer.usageName))
        case .renderScale(let scale): (name, value) = ("Render scale", GameSettings.renderScales.contains(scale) ? .number(scale) : nil)
        case .textureFiltering(let level): (name, value) = ("Texture sharpness at a distance", GameSettings.anisotropyLevels.contains(level) ? .number(level) : nil)
        case .frameCap(let cap): (name, value) = ("Frame cap", cap.flatMap { FrameCap.isPreset($0) ? .number($0) : nil })
        case .resolution: (name, value) = ("Resolution", nil)
        case .sensitivity: (name, value) = ("Sensitivity", nil)
        case .dpi: (name, value) = ("DPI", nil)
        case .volume: (name, value) = ("Game volume", nil)
        case .extraLaunchOptions: (name, value) = ("Extra launch options", nil)
        case .resetWarnings: (name, value) = ("Reset Warnings", nil)
        case .gameDataFolder: (name, value) = ("Game data folder", nil)
        case .cdKey: (name, value) = ("CD key", nil)
        case .cod2xMenus: (name, value) = ("CoD2x menus and icons", nil)
        }
        var fields: [String: UsageProperty] = ["setting": .text(name)]
        fields["value"] = value
        return fields
    }
}

enum UsagePreviewGroup: String, CaseIterable, Identifiable {
    case screens, setup, games, servers, playerSetups, hosting, updates, settings, problems, macAndCountry, everyEvent, never
    var id: Self { self }
}

/// The only public recording input. Case payloads contain fixed choices, numbers or a slug from the signed catalogue.
enum UsageEvent: Codable, Sendable {
    case appOpened
    case screenOpened(UsageScreen)
    case setupStepReached(UsageSetupStep, source: UsageDataSource? = nil)
    case setupStepSkipped(UsageSetupStep)
    case gameStarted(engine: GameEngine, renderer: GameRenderer, mode: UsageDisplayMode, scale: Int)
    case gameEnded(UsageGameEnd, minutes: Int)
    case serverListRefreshed(UsageServerList)
    case serverJoined(UsageJoinSource, engine: GameEngine?)
    case playerSetupViewed(ConfigDirectoryUsageID)
    case playerSetupTried(ConfigDirectoryUsageID)
    case playerSetupImported(ConfigDirectoryUsageID)
    case loadoutMade
    case checkRun
    case setupShared
    case setupWithdrawn
    case hostingStarted(HostedServer.Reach, engine: GameEngine)
    case updateOffered(UsageVersion, UsageVersion)
    case updateInstalled(UsageVersion, UsageVersion)
    case settingChanged(UsageSetting)
    case errorShown(UsageError)
    case usageDataTurnedOn(UsageConsentSource)

    fileprivate var payload: (name: String, fields: [String: UsageProperty]) {
        switch self {
        case .appOpened: ("app opened", [:])
        case .screenOpened(let screen): ("screen opened", ["screen": .text(screen.rawValue)])
        case .setupStepReached(let step, let source):
            ("setup step reached", source.map { ["step": .text(step.rawValue), "source": .text($0.rawValue)] } ?? ["step": .text(step.rawValue)])
        case .setupStepSkipped(let step): ("setup step skipped", ["step": .text(step.rawValue)])
        case .gameStarted(let engine, let renderer, let mode, let scale):
            ("game started", ["game": .text(engine.versionName), "renderer": .text(renderer.usageName), "display_mode": .text(mode.rawValue),
                              "render_scale": .number(renderer == .metal && GameSettings.renderScales.contains(scale) ? scale : 100)])
        case .gameEnded(let end, let minutes): ("game ended", ["how": .text(end.rawValue), "minutes": .number(max(0, minutes))])
        case .serverListRefreshed(let list): ("server list refreshed", ["list": .text(list.rawValue)])
        case .serverJoined(let how, let engine): ("server joined", engine.map { ["how": .text(how.rawValue), "game": .text($0.versionName)] } ?? ["how": .text(how.rawValue)])
        case .playerSetupViewed(let id): ("player setup viewed", ["setup_id": .text(id.rawValue)])
        case .playerSetupTried(let id): ("player setup tried", ["setup_id": .text(id.rawValue)])
        case .playerSetupImported(let id): ("player setup imported", ["setup_id": .text(id.rawValue)])
        case .loadoutMade: ("loadout made", [:])
        case .checkRun: ("check run", [:])
        case .setupShared: ("setup shared", [:])
        case .setupWithdrawn: ("setup withdrawn", [:])
        case .hostingStarted(let reach, let engine):
            ("hosting started", ["reach": .text(reach == .network ? "my network" : "the internet"), "game": .text(engine.versionName)])
        case .updateOffered(let from, let to): ("update offered", ["from": .text(from.label), "to": .text(to.label)])
        case .updateInstalled(let from, let to): ("update installed", ["from": .text(from.label), "to": .text(to.label)])
        case .settingChanged(let setting): ("setting changed", setting.fields)
        case .errorShown(let kind): ("error shown", ["kind": .text(kind.rawValue)])
        case .usageDataTurnedOn(let whereFrom): ("usage data turned on", ["where": .text(whereFrom.rawValue)])
        }
    }
}

private extension GameRenderer {
    var usageName: String { self == .metal ? "Metal" : "OpenGL" }
}

fileprivate enum UsageProperty: Encodable {
    case text(String), number(Int), flag(Bool)

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .text(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .flag(let value): try container.encode(value)
        }
    }
}

struct UsageFacts: Codable, Sendable {
    private let app: UsageVersion?
    private let osMajor: Int
    private let osMinor: Int
    private let osPatch: Int
    private let model: String
    private let chip: String
    private let width: Int
    private let height: Int
    private let hz: Int
    private let language: String

    /// The review harness's Mac: the boards' example (MacBook Pro, Apple M4 Pro, 3024 × 1964 at 120 Hz).
    static let preview = Self(app: UsageVersion("0.4.2"), osMajor: 27, osMinor: 0, osPatch: 0, model: "Mac16,6", chip: "Apple M4 Pro",
                              width: 3024, height: 1964, hz: 120, language: "en")

    @MainActor static func current(appVersion: String) -> Self {
        let os = ProcessInfo.processInfo.operatingSystemVersion
        let display = CGMainDisplayID()
        let mode = CGDisplayCopyDisplayMode(display)
        let refresh = mode?.refreshRate ?? 0
        let screen = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == display }
        return Self(app: UsageVersion(appVersion), osMajor: os.majorVersion, osMinor: os.minorVersion, osPatch: os.patchVersion,
                    model: sysctl("hw.model"), chip: sysctl("machdep.cpu.brand_string"),
                    width: mode?.pixelWidth ?? Int(CGDisplayPixelsWide(display)),
                    height: mode?.pixelHeight ?? Int(CGDisplayPixelsHigh(display)),
                    hz: refresh > 0 ? Int(refresh.rounded()) : screen?.maximumFramesPerSecond ?? 0,
                    language: Locale.preferredLanguages.first.map { Locale.Language(identifier: $0).languageCode?.identifier ?? "" } ?? "")
    }

    private static func sysctl(_ key: String) -> String {
        var size = 0
        guard sysctlbyname(key, nil, &size, nil, 0) == 0, (2...96).contains(size) else { return "" }
        var bytes = [UInt8](repeating: 0, count: size)
        guard sysctlbyname(key, &bytes, &size, nil, 0) == 0 else { return "" }
        return String(decoding: bytes.prefix { $0 != 0 }, as: UTF8.self)
    }

    // Check the system vocabulary again when reading a journal, as these are the only textual facts.
    private func formatted(_ value: String, as pattern: String) -> String {
        value.utf8.count <= 64 && value.range(of: pattern, options: .regularExpression) != nil ? value : "unknown"
    }

    fileprivate var fields: [String: UsageProperty] {
        ["app": .text(app?.label ?? "unknown"), "macos": .text("\(osMajor).\(osMinor).\(osPatch)"),
         "mac_model": .text(formatted(model, as: #"\A(?:Mac|MacBook(?:Pro|Air)?|Macmini|MacPro|MacStudio|VirtualMac|iMac(?:Pro)?)\d{1,3},\d{1,3}\z"#)),
         "chip": .text(formatted(chip, as: #"\AApple M[1-9][0-9]?(?: (?:Pro|Max|Ultra))?\z"#)),
         "display_width": .number(width), "display_height": .number(height), "display_hz": .number(hz),
         "language": .text(formatted(language, as: #"\A[a-z]{2,3}\z"#))]
    }
}

struct UsageOccurrence: Codable, Sendable {
    let id: UUID
    let minute: Int
    let event: UsageEvent
    let facts: UsageFacts

    func capture(for install: UUID) -> UsageCapture {
        let (name, specific) = event.payload
        var properties = facts.fields
        properties.merge(specific) { _, specific in specific }
        properties["$process_person_profile"] = .flag(false)
        properties["$lib"] = .text("cod2unbound-launcher")
        return UsageCapture(event: name, distinct_id: install.uuidString.lowercased(), uuid: id.uuidString.lowercased(),
                            timestamp: Date(timeIntervalSince1970: Double(minute) * 60).ISO8601Format(), properties: properties)
    }
}

struct UsageCapture: Encodable {
    let event: String
    let distinct_id: String
    let uuid: String
    let timestamp: String
    fileprivate let properties: [String: UsageProperty]
}
