import AppKit
import Foundation

@main @MainActor enum UsageDataTests {
    static func main() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let endpoint = try require(UsageEndpoint(key: "phc_fixture", override: ProcessInfo.processInfo.environment["COD2_USAGE_URL"]))
        try check(UsageEndpoint(key: "", override: endpoint.url.absoluteString) == nil, "missing key")
        try check(UsageEndpoint(key: "phc_fixture", override: "https://posthog.invalid/batch/") == nil, "non-loopback override")
        try check(UsageEndpoint(key: "phc_fixture", override: "http://127.0.0.1.example.com") == nil, "loopback prefix spoof")
        try check(UsageEndpoint(key: "phc_fixture", override: nil)?.url.absoluteString == "https://eu.i.posthog.com/batch/", "EU endpoint")
        let fixture = Fixture(root: root, endpoint: endpoint)
        try await consent(fixture)
        try await legacySendJournal(fixture)
        try await gameSessionConsent(fixture)
        try await retryAndRotation(fixture)
        try await bounds(fixture)
        try await privacy(fixture)
        try settings()
        try screens()
    }

    static func check(_ ok: Bool, _ message: String) throws {
        if !ok { throw Failure(message: message) }
    }

    static func require<T>(_ value: T?, _ message: String = "expected a value") throws -> T {
        guard let value else { throw Failure(message: message) }
        return value
    }

    struct Failure: Error, CustomStringConvertible {
        let message: String
        var description: String { message }
    }

    final class Clock {
        var now = Date(timeIntervalSince1970: 1_800_000_049)
        var forcedEventID: UUID?
        func nextEventID() -> UUID {
            defer { forcedEventID = nil }
            return forcedEventID ?? UUID()
        }
    }

    @MainActor struct Fixture {
        let root: URL
        let endpoint: UsageEndpoint
        let clock = Clock()
        var journal: URL { root.appendingPathComponent("requests.jsonl") }

        func usage(_ folder: String, _ consent: UsageConsent = .notAsked, enabled: Bool = true) -> UsageData {
            UsageData(home: root.appendingPathComponent(folder), consent: consent, endpoint: enabled ? endpoint : nil,
                      facts: { .current(appVersion: "0.4.2") }, now: { clock.now }, eventID: { clock.nextEventID() }, firstBatchDelay: .milliseconds(90), firstRetryDelay: 0.15)
        }

        func bodyLines() -> [String] {
            ((try? String(contentsOf: journal, encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
        }

        func events(since start: Int = 0) -> [[String: Any]] {
            bodyLines().dropFirst(start).flatMap { line -> [[String: Any]] in
                let packet = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any]
                return packet?["batch"] as? [[String: Any]] ?? []
            }
        }

        func script(_ mode: String) async throws {
            var request = URLRequest(url: endpoint.url.deletingLastPathComponent().appendingPathComponent("standin"))
            request.httpMethod = "POST"
            request.httpBody = try JSONEncoder().encode(["reply": mode])
            let (_, response) = try await URLSession.shared.data(for: request)
            try check((response as? HTTPURLResponse)?.statusCode == 200, "stand-in controls")
        }

        func wait(_ message: String, until condition: () -> Bool) async throws {
            for _ in 0..<180 {
                if condition() { return }
                try await Task.sleep(for: .milliseconds(20))
            }
            throw Failure(message: message)
        }
    }

    static func consent(_ f: Fixture) async throws {
        // Use the actual setup action, sheet answer and Settings binding. All share the real sender.
        _ = NSApplication.shared.setActivationPolicy(.prohibited)
        let model = LauncherModel()
        try check(model.home.path.hasPrefix(f.root.path + "/"), "launcher model uses the private test home")
        model.booted = true
        for source in [UsageConsentSource.setup, .sheet, .settings] {
            let start = f.bodyLines().count
            let folder = "consent-" + source.rawValue
            let home = f.root.appendingPathComponent(folder)
            let legacy = home.appendingPathComponent("usage-setup.json")
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
            let oldStep = UsageEvent.setupStepReached(.welcome)
            let minute = Int(f.clock.now.timeIntervalSince1970 / 60)
            let event = try JSONSerialization.jsonObject(with: JSONEncoder().encode(oldStep))
            let journal = try JSONSerialization.data(withJSONObject: ["schemaVersion": 1, "steps": [["minute": minute, "event": event]]])
            try journal.write(to: legacy)
            let usage = f.usage(folder)
            model.usageData = usage
            model.onboard = source == .setup
            usage.holdSending = model.onboard
            usage.applicationOpened()
            model.recordUsage(.screenOpened(.home))
            model.recordUsage(.setupStepReached(.gameData, source: .ownFiles))
            model.recordUsage(.setupStepSkipped(.cdKey))
            _ = usage.sampleEventJSON
            await usage.savePendingEvents()
            try check(f.bodyLines().count == start && usage.installNumber == nil &&
                      !FileManager.default.fileExists(atPath: usage.stateURL.path) &&
                      !FileManager.default.fileExists(atPath: legacy.path), "no send, install number or setup journal before consent")

            switch source {
            case .setup:
                model.setupStep = .usage
                model.nextSetup() // Records Ready after committing the answer.
            case .sheet:
                model.setUsageConsent(send: true, source: .sheet)
            case .settings:
                model.usageSwitch = true
            }
            if source != .setup { model.recordUsage(.setupStepReached(.ready)) }
            let number = try require(usage.installNumber)
            if source == .setup {
                try await Task.sleep(for: .milliseconds(300))
                try check(f.bodyLines().count == start, "nothing sent before setup ends")
                model.onboard = false
            }
            try await f.wait("batch did not drain after yes in \(source.rawValue)") { usage.queuedCount == 0 }
            let events = f.events(since: start)
            try check(events.compactMap { $0["event"] as? String } == ["usage data turned on", "setup step reached"],
                      "yes in \(source.rawValue) transmitted activity from before the answer")
            let properties = events.compactMap { $0["properties"] as? [String: Any] }
            try check(properties[0]["where"] as? String == source.rawValue && properties[1]["step"] as? String == "Ready",
                      "only the source of the yes and the step after the yes were sent")
            try check(events.allSatisfy { $0["distinct_id"] as? String == number.uuidString.lowercased() }, "one new install number")
            await usage.savePendingEvents()
            try check(!FileManager.default.fileExists(atPath: legacy.path), "legacy setup journal removed")
        }

        let start = f.bodyLines().count
        let accepted = f.usage("consent-settings", .yes)
        accepted.applicationOpened(); accepted.applicationOpened()
        try await f.wait("first post-consent launch missing") { accepted.queuedCount == 0 }
        await accepted.savePendingEvents()
        let sameDay = f.usage("consent-settings", .yes)
        sameDay.applicationOpened(); sameDay.applicationOpened()
        try check(sameDay.queuedCount == 0, "app-open suppressed across restarts and repeat calls")
        f.clock.now += 86_400
        sameDay.applicationOpened(); sameDay.applicationOpened()
        try await f.wait("next day app-open did not drain") { sameDay.queuedCount == 0 }
        try check(f.events(since: start).compactMap { $0["event"] as? String } == ["app opened", "app opened"],
                  "one event per UTC day opened after consent")

        let beforeNo = f.bodyLines().count
        let refused = f.usage("no")
        refused.applicationOpened(); refused.record(.setupStepReached(.welcome))
        refused.setConsent(.no, source: .sheet) { _ in }
        refused.record(.setupStepReached(.ready))
        await refused.savePendingEvents()
        try check(refused.installNumber == nil && refused.queuedCount == 0 && f.bodyLines().count == beforeNo, "no sent nothing")
        let unwritable = f.usage("settings-save-fails")
        unwritable.record(.setupStepReached(.welcome))
        let saved = unwritable.setConsent(.yes, source: .sheet) { _ in throw Failure(message: "write failed") }
        try check(!saved && unwritable.shouldAsk && unwritable.installNumber == nil &&
                  !FileManager.default.fileExists(atPath: unwritable.stateURL.path), "a failed preferences write cannot enable the sender")
        let disabled = f.usage("disabled", enabled: false)
        disabled.setConsent(.yes, source: .settings) { _ in throw Failure(message: "missing key persisted consent") }
        disabled.applicationOpened(); disabled.record(.setupStepReached(.welcome))
        await disabled.savePendingEvents()
        try check(!disabled.available && !disabled.shouldAsk && disabled.installNumber == nil &&
                  !FileManager.default.fileExists(atPath: disabled.stateURL.path), "a build with no key does not write a send journal")
        print("PASS: setup, one-time sheet and Settings send only events from consent onward, no sends nothing, daily app open")
    }

    static func gameSessionConsent(_ f: Fixture) async throws {
        enum Change: String, CaseIterable { case unchanged, offAndOn, reset }
        let model = LauncherModel()
        try check(model.home.path.hasPrefix(f.root.path + "/"), "game sessions use the private test home")
        model.booted = true

        func startGame() {
            model.gameRunning = true
            model.gameStartedAt = f.clock.now
            model.recordUsageGameStart(engine: .cod2x, renderer: .metal)
        }

        func endGame() {
            model.gameRunning = false
            model.recordUsageGameEnd(code: 0, at: model.gameStartedAt.addingTimeInterval(185))
        }

        func gameEvents(since start: Int) -> [[String: Any]] {
            f.events(since: start).filter { ["game started", "game ended"].contains($0["event"] as? String ?? "") }
        }

        for source in [UsageConsentSource.setup, .sheet, .settings] {
            let first = f.bodyLines().count
            let usage = f.usage("game-opt-in-" + source.rawValue)
            model.usageData = usage
            model.onboard = source == .setup
            startGame()
            switch source {
            case .setup:
                model.setupStep = .usage
                model.nextSetup()
                model.onboard = false
            case .sheet:
                model.setUsageConsent(send: true, source: .sheet)
            case .settings:
                model.usageSwitch = true
            }
            try check(usage.installNumber != nil, "yes granted during play in \(source.rawValue)")
            endGame()
            try await f.wait("mid-game opt-in batch did not drain") { usage.queuedCount == 0 }
            try check(gameEvents(since: first).isEmpty, "yes in \(source.rawValue) sent duration for a game started before consent")
        }

        for change in Change.allCases {
            let first = f.bodyLines().count
            let folder = "game-consent-" + change.rawValue
            let usage = f.usage(folder)
            model.usageData = usage
            model.usageSwitch = true
            let startNumber = try require(usage.installNumber)
            startGame()
            try await f.wait("game start did not drain") { usage.queuedCount == 0 }
            let started = gameEvents(since: first)
            try check(started.count == 1 && started.first?["event"] as? String == "game started" &&
                      started.first?["distinct_id"] as? String == startNumber.uuidString.lowercased(),
                      "one game start recorded with the original install number")

            switch change {
            case .offAndOn:
                model.usageSwitch = false
                model.usageSwitch = true
            case .reset:
                try check(model.resetUsageNumber(), "reset during play succeeded")
            case .unchanged:
                usage.holdSending = true // Exercise the recorded end after reopening the send journal.
            }
            let endNumber = try require(usage.installNumber)
            try check((endNumber == startNumber) == (change == .unchanged), "install number after \(change)")
            endGame()
            let sender: UsageData
            if change == .unchanged {
                await usage.savePendingEvents()
                sender = f.usage(folder, .yes)
                sender.applicationOpened()
            } else {
                sender = usage
            }
            try await f.wait("game end batch did not drain") { sender.queuedCount == 0 }
            let events = gameEvents(since: first)
            if change == .unchanged {
                let end = events.last
                let properties = end?["properties"] as? [String: Any]
                try check(events.count == 2 && end?["event"] as? String == "game ended" &&
                          end?["distinct_id"] as? String == startNumber.uuidString.lowercased() &&
                          properties?["minutes"] as? Int == 3 && properties?["how"] as? String == "closed normally",
                          "yes before launch sends start and end with the same install number and elapsed minutes")
            } else {
                try check(events.count == 1, "\(change) sent the old game's duration under a new install number")
            }
        }
        let first = f.bodyLines().count
        let attached = f.usage("game-already-running")
        model.usageData = attached
        model.gameRunning = true // No start event when attaching.
        model.gameStartedAt = f.clock.now
        model.usageSwitch = true
        endGame()
        try await f.wait("reattached game batch did not drain") { attached.queuedCount == 0 }
        try check(gameEvents(since: first).isEmpty, "reattaching never supplies a recorded game start")
        print("PASS: game duration requires a start under the same install number")
    }

    static func legacySendJournal(_ f: Fixture) async throws {
        let minute = Int(f.clock.now.timeIntervalSince1970 / 60)
        let events: [UsageEvent] = [
            .usageDataTurnedOn(.setup), .setupStepReached(.welcome), .appOpened, .setupStepReached(.ready),
            .gameStarted(engine: .cod2x, renderer: .metal, mode: .windowed, scale: 100),
            .gameEnded(.normal, minutes: 3), .loadoutMade,
        ]
        let occurrences = events.map { UsageOccurrence(id: UUID(), minute: minute, event: $0, facts: .preview) }
        let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(occurrences))
        for schema in [1, 2] {
            let folder = "legacy-send-\(schema)"
            let home = f.root.appendingPathComponent(folder)
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
            let install = UUID()
            let journal = try JSONSerialization.data(withJSONObject: [
                "schemaVersion": schema, "install": install.uuidString, "lastOpenedDay": minute / 1440, "events": encoded,
            ])
            try journal.write(to: home.appendingPathComponent("usage-data.json"))
            let start = f.bodyLines().count
            let usage = f.usage(folder, .yes)
            usage.applicationOpened()
            try await f.wait("legacy batch did not drain") { usage.queuedCount == 0 }
            let setupEvents = schema == 1 ? [] : ["setup step reached", "app opened", "setup step reached"]
            let sent = f.events(since: start).compactMap { $0["event"] as? String }
            try check(!sent.contains("game ended"), "schema \(schema) sent a pending game end of uncertain consent")
            try check(usage.installNumber == install &&
                      sent == ["usage data turned on"] + setupEvents + ["game started", "loadout made"],
                      "schema \(schema) keeps the install number and unambiguous events")
        }
        print("PASS: legacy send journal preserves the install number and unambiguous pending events")
    }

    static func retryAndRotation(_ f: Fixture) async throws {
        try await f.script("fail,ok")
        let usage = f.usage("retry")
        let start = f.bodyLines().count
        usage.setConsent(.yes, source: .sheet) { _ in }
        try await f.wait("no retry of failed batch") { f.bodyLines().count == start + 2 && usage.queuedCount == 0 }
        let attempts = Array(f.bodyLines().dropFirst(start))
        try check(attempts[0] == attempts[1], "retry reuses batch identifiers and timestamps")

        try await f.script("stall")
        let before = f.bodyLines().count
        usage.record(.loadoutMade)
        try await f.wait("stalling receiver didn't see the batch") { f.bodyLines().count > before }
        let initial = try require(usage.installNumber)
        usage.resetNumber()
        let rotated = try require(usage.installNumber)
        try check(initial != rotated && usage.consent == .yes && usage.queuedCount == 0, "reset replaces the number and clears the pending batch")
        let reopened = f.usage("retry", .yes)
        try check(reopened.installNumber == rotated && reopened.queuedCount == 0, "reset writes the new install, without the old queue")

        usage.record(.loadoutMade)
        usage.setConsent(.no, source: .settings) { _ in }
        try check(usage.installNumber == nil && usage.queuedCount == 0, "off deletes the active install and queue")
        try check(!FileManager.default.fileExists(atPath: usage.stateURL.path), "off deletes the active journal")
        let off = f.usage("retry", .no)
        try check(off.installNumber == nil && off.queuedCount == 0, "off survives restart")
        try await f.script("ok")
        let on = f.usage("retry", .no)
        on.setConsent(.yes, source: .settings) { _ in }
        try check(on.installNumber != rotated && on.installNumber != initial, "enabling makes another number")
        try await f.wait("reenabled batch did not drain") { on.queuedCount == 0 }
        print("PASS: failed POST retries byte-for-byte, stalled send cancellation, off, reset and re-enable")
    }

    static func bounds(_ f: Fixture) async throws {
        let usage = f.usage("bounds")
        let start = f.bodyLines().count
        usage.setConsent(.yes, source: .setup) { _ in }
        f.clock.now += 8 * 86_400
        for i in 0..<(UsageData.queueLimit + 8) { usage.record(.gameEnded(.normal, minutes: i)) }
        try check(usage.queuedCount == UsageData.queueLimit, "queue count capped")
        try await f.wait("queue not drained in batches") { usage.queuedCount == 0 }
        let events = f.events(since: start)
        try check(events.count == UsageData.queueLimit && events.allSatisfy { $0["event"] as? String == "game ended" }, "week-old entries and oldest overflow gone")
        let minutes = events.compactMap { ($0["properties"] as? [String: Any])?["minutes"] as? Int }
        try check(minutes == Array(8..<(UsageData.queueLimit + 8)), "only newest events sent in queue order")
        try check(f.bodyLines().count > start + 1, "sent multiple batches")
        print("PASS: event cap, expiry, oldest-first removal and batching")
    }

    static func privacy(_ f: Fixture) async throws {
        let directory = try DirectoryFixture.directory(cacheURL: f.root.appendingPathComponent("directory/index.json"))
        let index = try require(try await directory.load().snapshot)
        let id = try require(index.usageID("player-1"))
        let usage = f.usage("privacy")
        let start = f.bodyLines().count
        usage.setConsent(.yes, source: .settings) { _ in }
        let sampleID = UUID()
        f.clock.forcedEventID = sampleID
        let sample = try require(usage.sampleEventJSON)
        let sensitive = ["PRIVATE-HANDLE", "ABCD0123EF45678986D3", "203.0.113.97:28960", "/Users/private/game", "seta name \"SECRET\"", "private-slug"]
        var after = GameSettings()
        for value in sensitive {
            try check(index.usageID(value) == nil && UsageVersion(value) == nil && UsageDisplayMode(rawValue: value) == nil,
                      "untrusted string acquired a typed representation")
            try check(LauncherModel.usageJoinSource(address: value, pendingAddress: value, pendingSource: .link) == .link,
                      "address retained only to choose a typed join origin")
            after.fullscreen = value; after.resolution = value; after.advanced = value
            after.dismissedWarnings = [value]; after.dpi = 12_345
            for setting in UsageSetting.changes(from: .init(), to: after) { usage.record(.settingChanged(setting)) }
        }

        // Exercise model adapters that take actual player strings, then the closed inputs used by
        // lifecycle paths that cannot be driven without starting the game or opening windows.
        let profiles = f.root.appendingPathComponent("hostile-profiles")
        let store = try ConfigProfileStore(homeURL: profiles, dialect: .cod2x)
        let text = sensitive.joined(separator: " ")
        let config = Data(("seta sensitivity 4\n// \(text)\n").utf8)
        _ = try store.create(id: "Main", displayName: "Main", document: ConfigDocument(data: config, dialect: .cod2x))
        try store.select(id: "Main", expectedActive: nil)
        let session = try ConfigStudioSession(home: profiles, dialect: .cod2x, discoverGames: { [] })
        try session.refresh()
        let studio = ConfigStudioModel(session: session, dialect: .cod2x, catalog: { try ConfigStudioModel.bundledCatalog() })
        studio.announce = { _ in }
        studio.onLoadoutCreated = { usage.record(.loadoutMade) }
        try check(studio.createLoadout(named: sensitive[0], copyingCurrent: true), "hostile loadout name passed through the real creation action")
        let discover = ConfigDiscoverModel(studio: studio, directory: directory)
        discover.recordUsage = { usage.record($0) }
        await discover.reload()
        discover.checker.check(config, name: text, origin: text)
        try await f.wait("checker did not handle hostile content") { discover.checker.report != nil }
        discover.importText(String(decoding: config, as: UTF8.self), dropped: true)
        try await f.wait("ordinary hostile import did not start") { discover.review?.session != nil }
        try check(discover.review?.apply(undo: nil) != nil, "ordinary hostile import applied")
        discover.closeImport()
        discover.open(.player("player-1"))
        let entry = try require(discover.entry("player-1"))
        discover.tryEntry(entry)
        try await f.wait("verified catalogue import did not start") { discover.review?.session != nil }
        try check(discover.review?.apply(undo: nil) != nil, "verified catalogue import applied")
        discover.closeImport()
        let untrustedReport = URL(fileURLWithPath: "/Users/private/game/cod2x_freeze.123.log")
        try check(UsageGameEnd.observed(code: 9, requestedQuit: false, reports: [untrustedReport]) == .freeze &&
                  UsageGameEnd.observed(code: nil, requestedQuit: false, reports: []) == nil &&
                  UsageGameEnd.observed(code: SIGTERM, requestedQuit: true, reports: []) == .normal, "game outcome mapping only observes known signs")

        let launcherEvents: [UsageEvent] = [
            .screenOpened(.configsPlayerSetup), .setupStepReached(.gameData, source: .steam), .setupStepSkipped(.cdKey),
            .gameStarted(engine: .cod2x, renderer: .metal, mode: .windowed, scale: 75), .gameEnded(.freeze, minutes: 3),
            .serverListRefreshed(.favorites), .serverJoined(.list, engine: .cod2x),
            .playerSetupViewed(id), .playerSetupTried(id), .playerSetupImported(id), .loadoutMade, .checkRun, .setupShared,
            .setupWithdrawn, .hostingStarted(.network, engine: .original),
            .updateOffered(try require(UsageVersion("0.4.2")), try require(UsageVersion("0.4.3"))),
            .updateInstalled(try require(UsageVersion("0.4.2")), try require(UsageVersion("0.4.3"))),
            .settingChanged(.extraLaunchOptions), .settingChanged(.toggle(.rawMouse, true)), .errorShown(.gameCouldNotStart),
            .setupStepReached(.welcome), .serverJoined(.typedAddress, engine: nil)
        ]
        usage.applicationOpened()
        for event in launcherEvents {
            if case .serverJoined(.list, _) = event { f.clock.forcedEventID = sampleID }
            usage.record(event)
        }
        try await f.wait("all event types did not send") { usage.queuedCount == 0 }
        let bytes = f.bodyLines().dropFirst(start).joined(separator: "\n")
        try check(sensitive.allSatisfy { !bytes.contains($0) } && !bytes.contains("$ip") && !bytes.contains("$geoip_disable"), "unexpected data in payload")
        let events = f.events(since: start)
        try disclosure(events, for: launcherEvents + [.appOpened, .usageDataTurnedOn(.settings)])
        for event in events {
            let properties = try require(event["properties"] as? [String: Any])
            try check(properties["$process_person_profile"] as? Bool == false && properties["$lib"] as? String == "cod2unbound-launcher",
                      "anonymous capture properties")
            try check((event["timestamp"] as? String)?.hasSuffix(":00Z") == true, "timestamp only to the minute")
        }
        let sent = try require(events.first { $0["uuid"] as? String == sampleID.uuidString.lowercased() })
        let preview = try require(JSONSerialization.jsonObject(with: Data(sample.utf8)) as? [String: Any])
        try check(NSDictionary(dictionary: preview).isEqual(to: sent), "preview event and actual batch event match field for field")
        print("PASS: all 21 event kinds and every wire property mapped to disclosure rows, hostile inputs, exact wire preview")
    }

    private typealias Disclosure = (name: String, row: UsagePreviewGroup, fields: Set<String>)

    /// Keep this exhaustive so a new event case requires a disclosure row even before adding a fixture.
    private static func disclosure(for event: UsageEvent) -> Disclosure {
        switch event {
        case .appOpened: ("app opened", .screens, [])
        case .screenOpened: ("screen opened", .screens, ["screen"])
        case .setupStepReached: ("setup step reached", .setup, ["step", "source"])
        case .setupStepSkipped: ("setup step skipped", .setup, ["step"])
        case .gameStarted: ("game started", .games, ["game", "renderer", "display_mode", "render_scale"])
        case .gameEnded: ("game ended", .games, ["how", "minutes"])
        case .serverListRefreshed: ("server list refreshed", .servers, ["list"])
        case .serverJoined: ("server joined", .servers, ["how", "game"])
        case .playerSetupViewed: ("player setup viewed", .playerSetups, ["setup_id"])
        case .playerSetupTried: ("player setup tried", .playerSetups, ["setup_id"])
        case .playerSetupImported: ("player setup imported", .playerSetups, ["setup_id"])
        case .loadoutMade: ("loadout made", .playerSetups, [])
        case .checkRun: ("check run", .playerSetups, [])
        case .setupShared: ("setup shared", .playerSetups, [])
        case .setupWithdrawn: ("setup withdrawn", .playerSetups, [])
        case .hostingStarted: ("hosting started", .hosting, ["reach", "game"])
        case .updateOffered: ("update offered", .updates, ["from", "to"])
        case .updateInstalled: ("update installed", .updates, ["from", "to"])
        case .settingChanged: ("setting changed", .settings, ["setting", "value"])
        case .errorShown: ("error shown", .problems, ["kind"])
        case .usageDataTurnedOn: ("usage data turned on", .everyEvent, ["where"])
        }
    }

    private static func disclosure(_ captures: [[String: Any]], for events: [UsageEvent]) throws {
        let rows = Set(UsagePreviewGroup.allCases)
        let envelope: [UsagePreviewGroup: Set<String>] = [.everyEvent: ["event", "distinct_id", "uuid", "timestamp", "properties"]]
        let shared: [UsagePreviewGroup: Set<String>] = [
            .macAndCountry: ["app", "macos", "mac_model", "chip", "display_width", "display_height", "display_hz", "language"],
            .everyEvent: ["$lib", "$process_person_profile"],
        ]
        let envelopeFields = Set(envelope.values.flatMap { $0 })
        let sharedFields = Set(shared.values.flatMap { $0 })
        let declarations = events.map { disclosure(for: $0) }
        let used = Set(declarations.map(\.row)).union(envelope.keys).union(shared.keys)
        try check(used == rows.subtracting([.never]), "each disclosure row accounts for its named events or fields")
        try check(used.allSatisfy { !UsageDataQuestion.group($0).detail.isEmpty }, "wire fields map to visible disclosure text")

        var actualFields: [String: Set<String>] = [:]
        for capture in captures {
            try check(Set(capture.keys) == envelopeFields, "unaccounted event envelope field")
            let name = try require(capture["event"] as? String)
            let fields = Set(try require(capture["properties"] as? [String: Any]).keys)
            try check(sharedFields.isSubset(of: fields), "missing shared property in \(name)")
            actualFields[name, default: []].formUnion(fields.subtracting(sharedFields))
        }
        try check(Set(actualFields.keys) == Set(declarations.map(\.name)), "Every wire event needs a disclosure row")
        for entry in declarations {
            try check(actualFields[entry.name] == entry.fields,
                      "Properties for \(entry.name) changed. Review the \(UsageDataQuestion.group(entry.row).title) disclosure row.")
        }
    }

    /// What the usage screens read from the model: the fixed example, the preview state the harness
    /// drives, the words for every See What’s Sent group, and setup's eighth step.
    static func screens() throws {
        let install = UUID(uuidString: "7F3C9A2E-41D8-4B6E-9C05-2D8E61A4B7F0")!
        let id = UUID(uuidString: "2B9D3E51-7C0A-4F6B-9E21-5A8C4D7F0E13")!
        let before = try require(UsageData.sampleJSON(facts: .preview, install: nil, minute: 29_921_534, id: id))
        let after = try require(UsageData.sampleJSON(facts: .preview, install: install, minute: 29_921_534, id: id))
        let a = try require(JSONSerialization.jsonObject(with: Data(before.utf8)) as? [String: Any])
        let b = try require(JSONSerialization.jsonObject(with: Data(after.utf8)) as? [String: Any])
        try check(a["event"] as? String == "server joined" && a["distinct_id"] as? String == "00000000-0000-0000-0000-000000000000",
                  "before a yes the example carries the all-zero number")
        try check(b["distinct_id"] as? String == "7f3c9a2e-41d8-4b6e-9c05-2d8e61a4b7f0" && b["timestamp"] as? String == "2026-11-21T20:14:00Z",
                  "after a yes it carries the install number, at the board's minute")
        let properties = try require(b["properties"] as? [String: Any])
        try check(properties["how"] as? String == "from the list" && properties["chip"] as? String == "Apple M4 Pro" && properties["app"] as? String == "0.4.2",
                  "the example is a server joined from the list on the boards' Mac")
        try check(!after.contains("address") && !after.contains("hostname") && !after.contains("$ip"), "the example names no server and no address")

        try check(UsagePreviewGroup.allCases.map { UsageDataQuestion.group($0).title } == ["Screens", "Setup", "Games", "Servers", "Player setups", "Hosting", "Updates", "Settings", "Problems", "This Mac, your country", "With every event", "Never"],
                  "See What’s Sent lists the twelve groups in the board's order")
        try check(UsageDataQuestion.rows.map(\.title) == ["What is sent", "What never is", "Where it goes"], "the question's three rows")

        let model = LauncherModel(snapshot: true)
        try check(!model.usageAvailable && model.usageSampleJSON == nil && !model.privacyContacts.contains { $0.who == "PostHog, in the EU" },
                  "a build without the key offers nothing and names no PostHog")
        model.onboard = true
        model.previewUsage = (.notAsked, nil)
        try check(model.shouldAskUsageInSetup && model.setupSteps.count == 8 && model.setupSteps[6] == .usage, "a due question adds step 7 of 8")
        model.setupStep = .crash
        model.alternateSetup()
        try check(model.setupStep == .usage && model.setupPrimaryTitle == "Send Usage Data" && model.setupAlternateTitle == "Don’t Send",
                  "Crash reports leads to Usage data, with its two answers")
        model.nextSetup()
        try check(model.setupStep == .ready && model.usageConsent == .yes && model.usageInstallNumberShort?.count == 9 && model.setupSteps.count == 8,
                  "Send Usage Data answers, makes a number and keeps eight steps")
        try check(model.privacyContacts.last?.who == "PostHog, in the EU", "What CoD2 Unbound contacts names PostHog")
        let first = model.usageInstallNumberFull
        try check(model.resetUsageNumber() && model.usageInstallNumberFull != first, "Reset Number makes a new number")
        model.usageSwitch = false
        try check(model.usageConsent == .no && model.usageInstallNumberShort == nil, "off forgets the number")
        model.restartSetup(at: .data)
        try check(!model.setupSteps.contains(.usage) && model.setupSteps.count == 7, "once answered, setup is seven steps again")
        print("PASS: usage screens' model: fixed example through the sender's encoder, ordered disclosure rows, preview answers, setup step 7 of 8")
    }

    static func settings() throws {
        let old = try JSONDecoder().decode(GameSettings.self, from: Data("{}".utf8))
        try check(old.usageConsent == .notAsked, "older settings default to not asked")
        for answer in [UsageConsent.yes, .no] {
            var settings = old
            settings.usageConsent = answer
            let saved = try JSONDecoder().decode(GameSettings.self, from: JSONEncoder().encode(settings))
            try check(saved.reset(to: .init()).usageConsent == answer, "Reset Settings keeps each usage answer")
        }
        print("PASS: old settings decode and Reset Settings preserves usage consent")
    }
}
