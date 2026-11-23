import Foundation

struct UsageEndpoint: Sendable {
    static let infoKey = "CoD2PostHogKey"
    let url: URL
    private let key: String

    init?(key: String, override: String?) {
        guard key.range(of: #"\Aphc_[A-Za-z0-9_-]{1,200}\z"#, options: .regularExpression) != nil else { return nil }
        self.key = key
        if let override {
            guard var local = URLComponents(string: override), ["https", "http"].contains(local.scheme),
                  local.host == "127.0.0.1", local.user == nil, local.password == nil,
                  local.query == nil, local.fragment == nil,
                  ["", "/", "/batch", "/batch/"].contains(local.percentEncodedPath) else { return nil }
            local.path = "/batch/"
            guard let url = local.url else { return nil }
            self.url = url
        } else {
            url = URL(string: "https://eu.i.posthog.com/batch/")!
        }
    }

    private struct Batch: Encodable {
        let api_key: String
        let batch: [UsageCapture]
    }

    private final class NoRedirects: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest) async -> URLRequest? { nil }
    }

    nonisolated func send(_ events: [UsageOccurrence], install: UUID) async -> Bool {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 20
        let session = URLSession(configuration: configuration)
        defer { session.finishTasksAndInvalidate() }
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            let body = try encoder.encode(Batch(api_key: key, batch: events.map { $0.capture(for: install) }))
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("cod2unbound-launcher", forHTTPHeaderField: "User-Agent")
            request.httpBody = body
            try Task.checkCancellation()
            let (_, response) = try await session.data(for: request, delegate: NoRedirects())
            return (response as? HTTPURLResponse).map { (200..<300).contains($0.statusCode) } ?? false
        } catch {
            return false
        }
    }
}

/// Main-actor consent and queue state, one per launcher. Disk writes run serially and network
/// work never blocks the main actor. Events enter the queue only after consent.
@MainActor final class UsageData {
    static let queueLimit = 300
    private static let batchLimit = 32
    private static let maxAgeMinutes = 7 * 24 * 60

    private struct SendJournal: Codable, Sendable {
        static let currentSchemaVersion = 2
        var schemaVersion = Self.currentSchemaVersion
        var install: UUID?
        /// Local guard against counting this UTC day twice, including across number resets.
        var lastOpenedDay: Int?
        var events: [UsageOccurrence] = []
    }

    let stateURL: URL
    private let endpoint: UsageEndpoint?
    private let now: () -> Date
    private let facts: () -> UsageFacts
    private let eventID: () -> UUID
    private let firstBatchDelay: Duration
    private let firstRetryDelay: TimeInterval
    private let disk = DispatchQueue(label: "com.cod2unbound.usage.disk", qos: .utility)
    private var state = SendJournal()
    private var sendTask: Task<Void, Never>?
    private var sendGeneration = UUID()
    private var retryDelay: TimeInterval
    private(set) var consent: UsageConsent
    var available: Bool { endpoint != nil }
    var shouldAsk: Bool { available && consent == .notAsked }
    var installNumber: UUID? { available && consent == .yes ? state.install : nil }
    var queuedCount: Int { state.events.count }

    init(home: URL, consent: UsageConsent, endpoint: UsageEndpoint?, facts: @escaping () -> UsageFacts,
         now: @escaping () -> Date = Date.init, eventID: @escaping () -> UUID = UUID.init,
         firstBatchDelay: Duration = .seconds(2), firstRetryDelay: TimeInterval = 5) {
        self.endpoint = endpoint; self.consent = consent; self.facts = facts; self.now = now; self.eventID = eventID
        self.firstBatchDelay = firstBatchDelay; self.firstRetryDelay = firstRetryDelay; retryDelay = firstRetryDelay
        stateURL = home.appendingPathComponent("usage-data.json")
        // Earlier launchers recorded setup before the player answered. Never read that journal.
        try? Self.remove(home.appendingPathComponent("usage-setup.json"))
        guard available else { return }
        if let stored: SendJournal = Self.read(stateURL, limit: 512 * 1024),
           stored.schemaVersion == 1 || stored.schemaVersion == SendJournal.currentSchemaVersion {
            state.lastOpenedDay = stored.lastOpenedDay
            if consent == .yes {
                state = stored
                if stored.schemaVersion == 1 {
                    // The old queue mixed replayed steps and a synthetic app open with real
                    // post-consent events. They may even share a minute, so drop ambiguous entries.
                    state.events.removeAll {
                        switch $0.event {
                        case .setupStepReached, .setupStepSkipped, .appOpened: true
                        default: false
                        }
                    }
                    state.schemaVersion = SendJournal.currentSchemaVersion
                }
                prune()
            }
        }
        if consent == .yes, state.install == nil {
            // An interrupted write or a lost local journal cannot silently create a new identity.
            self.consent = .no
        }
        if self.consent != .yes { cleanInactiveJournal() }
    }

    /// The preview uses the same event record and encoder as the batch. Before consent an all-zero
    /// example replaces the install number. Previewing never creates or saves a real number.
    var sampleEventJSON: String? {
        guard available else { return nil }
        return Self.sampleJSON(facts: facts(), install: installNumber, minute: minute, id: eventID())
    }

    /// See What’s Sent’s example, a server joined from the list, through the sender's own encoder.
    /// Without a number (before a yes) the all-zero example stands in for it.
    nonisolated static func sampleJSON(facts: UsageFacts, install: UUID?, minute: Int, id: UUID) -> String? {
        let example = UsageOccurrence(id: id, minute: minute, event: .serverJoined(.list, engine: .cod2x), facts: facts)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let number = install ?? UUID(uuidString: "00000000-0000-0000-0000-000000000000")!
        return (try? encoder.encode(example.capture(for: number))).map { String(decoding: $0, as: UTF8.self) }
    }

    /// The persistence closure writes the answer to launcher-settings.json. Sending starts only once
    /// that write and the install journal have both succeeded. Turning off cancels first.
    @discardableResult func setConsent(_ answer: UsageConsent, source: UsageConsentSource,
                                       persist: (UsageConsent) throws -> Void) -> Bool {
        guard available, answer != .notAsked else { return false }
        guard answer != consent else { return true }
        if answer == .no {
            cancelSend()
            consent = .no
            state.install = nil
            state.events = []
            let cleared = cleanInactiveJournal()
            do { try persist(.no) } catch { return false }
            return cleared
        }

        let install = UUID(), stamp = minute, machine = facts()
        var candidate = SendJournal(install: install, lastOpenedDay: state.lastOpenedDay)
        candidate.events.append(occurrence(.usageDataTurnedOn(source), minute: stamp, facts: machine))
        guard saveSynchronously(candidate) else { return false }
        do { try persist(.yes) } catch {
            cleanInactiveJournal()
            return false
        }
        state = candidate; consent = .yes
        scheduleSend(after: firstBatchDelay)
        return true
    }

    @discardableResult func resetNumber() -> Bool {
        guard installNumber != nil else { return false }
        let fresh = SendJournal(install: UUID(), lastOpenedDay: state.lastOpenedDay)
        cancelSend()
        guard saveSynchronously(fresh) else {
            scheduleSend(after: firstBatchDelay)
            return false
        }
        state = fresh
        return true
    }

    func applicationOpened() {
        guard installNumber != nil else { return }
        let stamp = minute, day = stamp / 1440
        if shouldCountOpen(day: day) {
            state.lastOpenedDay = day
            enqueue(.appOpened, at: stamp)
        } else {
            prune()
            saveState()
            scheduleSend(after: firstBatchDelay)
        }
    }

    func record(_ event: UsageEvent) {
        guard installNumber != nil else { return }
        switch event {
        case .appOpened, .usageDataTurnedOn: return // Only the consent and app lifecycle own these.
        default: break
        }
        enqueue(event, at: minute)
    }

    /// Lets the updater finish its local bookkeeping before it asks the app to relaunch.
    /// Ordinary launches and quits never wait for this.
    func savePendingEvents() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            disk.async { continuation.resume() }
        }
    }

    private var minute: Int { Int(floor(now().timeIntervalSince1970 / 60)) }
    private func shouldCountOpen(day: Int) -> Bool { state.lastOpenedDay.map { day > $0 } ?? true }
    private func occurrence(_ event: UsageEvent, minute: Int, facts: UsageFacts) -> UsageOccurrence {
        UsageOccurrence(id: eventID(), minute: minute, event: event, facts: facts)
    }
    private func enqueue(_ event: UsageEvent, at stamp: Int) {
        state.events.append(occurrence(event, minute: stamp, facts: facts()))
        prune()
        saveState()
        scheduleSend(after: firstBatchDelay)
    }

    private func prune() {
        let stamp = minute
        state.events.removeAll { $0.minute < stamp - Self.maxAgeMinutes || $0.minute > stamp }
        state.events = Array(state.events.suffix(Self.queueLimit))
    }
    private func saveState() {
        let state = self.state, url = stateURL
        disk.async { _ = Self.write(state, to: url) }
    }
    private func saveSynchronously(_ candidate: SendJournal) -> Bool {
        let url = stateURL
        return disk.sync { Self.write(candidate, to: url) }
    }

    @discardableResult private func cleanInactiveJournal() -> Bool {
        let state = self.state, url = stateURL
        return disk.sync {
            if state.lastOpenedDay != nil, Self.write(state, to: url) { return true }
            return (try? Self.remove(url)) != nil
        }
    }

    private func cancelSend() {
        sendGeneration = UUID()
        sendTask?.cancel(); sendTask = nil
        retryDelay = firstRetryDelay
    }
    /// Setup's welcome says nothing leaves the Mac during setup: a yes given in setup is kept on the
    /// Mac until setup ends, then sent.
    var holdSending = false {
        didSet {
            guard holdSending != oldValue else { return }
            if holdSending { cancelSend() } else { scheduleSend(after: firstBatchDelay) }
        }
    }

    private func scheduleSend(after delay: Duration) {
        guard !holdSending, sendTask == nil, installNumber != nil, !state.events.isEmpty else { return }
        let generation = sendGeneration
        sendTask = Task(priority: .utility) { [weak self] in
            do { try await Task.sleep(for: delay) } catch { return }
            await self?.sendBatch(generation: generation)
        }
    }

    private func sendBatch(generation: UUID) async {
        guard generation == sendGeneration, let install = installNumber, let endpoint else { return }
        prune()
        let current = state, url = stateURL
        let persisted = await withCheckedContinuation { continuation in
            disk.async { continuation.resume(returning: Self.write(current, to: url)) }
        }
        guard generation == sendGeneration, !Task.isCancelled else { return }
        let batch = Array(current.events.prefix(Self.batchLimit))
        let sent: Bool
        if persisted, !batch.isEmpty { sent = await endpoint.send(batch, install: install) }
        else { sent = batch.isEmpty }
        guard generation == sendGeneration, !Task.isCancelled else { return }
        sendTask = nil
        if sent {
            let accepted = Set(batch.map(\.id))
            state.events.removeAll { accepted.contains($0.id) }
            saveState()
            retryDelay = firstRetryDelay
            scheduleSend(after: .zero)
        } else {
            scheduleSend(after: .seconds(retryDelay))
            retryDelay = min(3600, retryDelay * 2)
        }
    }

    private nonisolated static func read<T: Decodable>(_ file: URL, limit: Int) -> T? {
        guard let handle = try? FileHandle(forReadingFrom: file) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: limit + 1), data.count <= limit else { return nil }
        return try? JSONDecoder().decode(T.self, from: data)
    }
    private nonisolated static func write<T: Encodable>(_ data: T, to file: URL) -> Bool {
        do { try PrivateFile.write(JSONEncoder().encode(data), to: file); return true }
        catch { return false }
    }
    private nonisolated static func remove(_ file: URL) throws {
        do { try FileManager.default.removeItem(at: file) }
        catch let error as CocoaError where error.code == .fileNoSuchFile { }
    }
}
