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
        let first = f.usage("yes")
        first.applicationOpened()
        first.record(.screenOpened(.home))
        _ = first.sampleEventJSON
        await first.savePendingEvents()
        try check(f.bodyLines().isEmpty && first.installNumber == nil && !FileManager.default.fileExists(atPath: first.stateURL.path)
                  && !FileManager.default.fileExists(atPath: first.setupURL.path), "ordinary activity and preview did not write before consent")
        first.record(.setupStepReached(.gameData, source: .ownFiles))
        await first.savePendingEvents()
        try check(FileManager.default.fileExists(atPath: first.setupURL.path) && !FileManager.default.fileExists(atPath: first.stateURL.path), "only local setup journal before consent")
        f.clock.now += 125
        let restarted = f.usage("yes")
        restarted.applicationOpened()
        restarted.setConsent(.yes, source: .setup) { _ in }
        let number = try require(restarted.installNumber)
        try await f.wait("no batch after yes") { f.events().count == 3 }
        try await f.wait("accepted batch did not drain") { restarted.queuedCount == 0 }
        let events = f.events()
        try check(events.compactMap { $0["event"] as? String } == ["usage data turned on", "setup step reached", "app opened"], "consent first, persisted setup second")
        try check(events.allSatisfy { $0["distinct_id"] as? String == number.uuidString.lowercased() }, "one new install number")
        try check(events[1]["timestamp"] as? String == "2027-01-15T08:00:00Z", "original setup minute")
        try check(!FileManager.default.fileExists(atPath: restarted.setupURL.path), "setup journal cleared after yes")
        await restarted.savePendingEvents()
        let sameDay = f.usage("yes", .yes)
        sameDay.applicationOpened(); sameDay.applicationOpened()
        try check(sameDay.queuedCount == 0, "app-open suppressed across restarts and repeat calls")
        f.clock.now += 86_400
        sameDay.applicationOpened(); sameDay.applicationOpened()
        try await f.wait("next day app-open missing") { f.events().count == 4 }
        try await f.wait("next day app-open did not drain") { sameDay.queuedCount == 0 }

        let refused = f.usage("no")
        refused.applicationOpened(); refused.record(.setupStepReached(.welcome))
        refused.setConsent(.no, source: .sheet) { _ in }
        refused.record(.setupStepReached(.ready))
        await refused.savePendingEvents()
        try check(!FileManager.default.fileExists(atPath: refused.setupURL.path) && refused.installNumber == nil && refused.queuedCount == 0, "no clears the setup journal and discards later steps")
        try check(f.events().count == 4, "no sent nothing")
        let unwritable = f.usage("settings-save-fails")
        unwritable.record(.setupStepReached(.welcome))
        unwritable.applicationOpened()
        let accepted = unwritable.setConsent(.yes, source: .sheet) { _ in throw Failure(message: "write failed") }
        try check(!accepted && unwritable.shouldAsk && unwritable.installNumber == nil &&
                  !FileManager.default.fileExists(atPath: unwritable.stateURL.path), "a failed preferences write cannot enable the sender")
        try check(FileManager.default.fileExists(atPath: unwritable.setupURL.path) && f.events().count == 4,
                  "failed opt-in leaves only the local setup history")
        let disabled = f.usage("disabled", enabled: false)
        disabled.setConsent(.yes, source: .settings) { _ in throw Failure(message: "missing key persisted consent") }
        disabled.applicationOpened(); disabled.record(.setupStepReached(.welcome))
        await disabled.savePendingEvents()
        try check(!disabled.available && !disabled.shouldAsk && disabled.installNumber == nil &&
                  !FileManager.default.fileExists(atPath: disabled.setupURL.path), "no key disables usage data and its local journals")
        print("PASS: consent, pre-question journal, unavailable build, daily app open")

        // A yes given in setup waits on the Mac until setup ends (the welcome says nothing leaves during setup).
        let sentBefore = f.events().count
        let inSetup = f.usage("held-in-setup")
        inSetup.holdSending = true
        inSetup.record(.setupStepReached(.welcome))
        inSetup.setConsent(.yes, source: .setup) { _ in }
        inSetup.record(.setupStepReached(.ready))
        try await Task.sleep(for: .milliseconds(450))
        try check(f.events().count == sentBefore && inSetup.queuedCount == 3, "nothing is sent while setup is open")
        inSetup.holdSending = false
        try await f.wait("held events were not sent after setup") { f.events().count == sentBefore + 3 }
        try await f.wait("held events did not drain") { inSetup.queuedCount == 0 }
        print("PASS: a yes in setup is held on the Mac until setup ends, then sent")
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
        for _ in 0..<(UsageData.setupLimit + 3) { usage.record(.setupStepReached(.welcome)) }
        await usage.savePendingEvents()
        let local = try require(JSONSerialization.jsonObject(with: Data(contentsOf: usage.setupURL)) as? [String: Any])
        try check((local["steps"] as? [Any])?.count == UsageData.setupLimit, "setup journal has a small cap")
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
        print("PASS: setup cap, event cap, expiry, oldest-first removal and batching")
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
            .settingChanged(.extraLaunchOptions), .errorShown(.gameCouldNotStart)
        ]
        usage.applicationOpened()
        for event in launcherEvents {
            if case .serverJoined = event { f.clock.forcedEventID = sampleID }
            usage.record(event)
        }
        try await f.wait("all event types did not send") { usage.queuedCount == 0 }
        let bytes = f.bodyLines().dropFirst(start).joined(separator: "\n")
        try check(sensitive.allSatisfy { !bytes.contains($0) } && !bytes.contains("$ip") && !bytes.contains("$geoip_disable"), "unexpected data in payload")
        let events = f.events(since: start)
        try check(Set(events.compactMap { $0["event"] as? String }).count == 21, "every event kind was sent")
        for event in events {
            let properties = try require(event["properties"] as? [String: Any])
            try check(properties["$process_person_profile"] as? Bool == false && properties["$lib"] as? String == "cod2unbound-launcher",
                      "anonymous capture properties")
            try check(["app", "macos", "mac_model", "chip", "display_width", "display_height", "display_hz", "language"].allSatisfy { properties[$0] != nil },
                      "all device facts are present")
            try check((event["timestamp"] as? String)?.hasSuffix(":00Z") == true, "timestamp only to the minute")
        }
        let sent = try require(events.first { $0["event"] as? String == "server joined" })
        let preview = try require(JSONSerialization.jsonObject(with: Data(sample.utf8)) as? [String: Any])
        try check(NSDictionary(dictionary: preview).isEqual(to: sent), "preview event and actual batch event match field for field")
        print("PASS: all 21 event kinds, hostile inputs, exact wire preview and shared device metadata")
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

        try check(UsagePreviewGroup.allCases.map { UsageDataQuestion.group($0).title } == ["Screens and features", "Settings", "This Mac, your country", "Problems", "Never"],
                  "See What’s Sent lists the five groups in the board's order")
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
        print("PASS: usage screens' model: fixed example through the sender's encoder, five groups, preview answers, setup step 7 of 8")
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
