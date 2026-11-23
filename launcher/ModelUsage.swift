import Foundation

@MainActor extension LauncherModel {
    // MARK: Privacy and setup presentation

    var usageConsent: UsageConsent { previewUsage?.consent ?? usageData.consent }
    var usageAvailable: Bool { previewUsage != nil || usageData.available }
    var shouldAskUsageInSetup: Bool { onboard && (previewUsage.map { $0.consent == .notAsked } ?? usageData.shouldAsk) }
    var usageSampleJSON: String? {
        guard let previewUsage else { return usageData.sampleEventJSON }
        // The harness's fixed example: the boards' Mac, 21 November 2026 at 20:14 UTC.
        return UsageData.sampleJSON(facts: .preview, install: previewUsage.install, minute: 29_921_534,
                                    id: UUID(uuidString: "2B9D3E51-7C0A-4F6B-9E21-5A8C4D7F0E13")!)
    }
    var usagePreviewGroups: [UsagePreviewGroup] { UsagePreviewGroup.allCases }
    /// The number shown under the switch: its first eight characters, as the board shows it.
    var usageInstallNumberShort: String? { (previewUsage?.install ?? usageInstallNumber).map { String($0.uuidString.lowercased().prefix(8)) + "…" } }
    /// The full number, for VoiceOver and the row's help.
    var usageInstallNumberFull: String? { (previewUsage?.install ?? usageInstallNumber)?.uuidString.lowercased() }
    /// Settings › Privacy's switch.
    var usageSwitch: Bool {
        get { usageConsent == .yes }
        set { setUsageConsent(send: newValue, source: .settings) }
    }
    /// What CoD2 Unbound contacts, with PostHog's line in a build that can send usage data.
    var privacyContacts: [(who: String, when: String)] {
        PrivacyContacts.lines + (usageAvailable ? [UsageDataQuestion.contact] : [])
    }

    @discardableResult func setUsageConsent(send: Bool, source: UsageConsentSource) -> Bool {
        if let preview = previewUsage {
            // The review harness answers without the store: a yes makes a number, off forgets it.
            previewUsage = (send ? .yes : .no, send ? (preview.install ?? UUID()) : nil)
            usageQuestionPending = false
            return true
        }
        let ok = usageData.setConsent(send ? .yes : .no, source: source) { answer in
            var candidate = settings
            candidate.usageConsent = answer
            // Write the answer with the other preferences, independently of generating launcher.cfg.
            try writeLauncherSettings(candidate)
            settings = candidate
            usageSettingsBaseline = candidate
        }
        if usageData.consent == .no && usageAvailable && settings.usageConsent != .no {
            // Sending was disabled immediately even if the preferences could not be written.
            settings.usageConsent = .no
        }
        usageInstallNumber = usageData.installNumber
        if usageData.consent != .notAsked { usageQuestionPending = false }
        return ok
    }

    @discardableResult func resetUsageNumber() -> Bool {
        if let preview = previewUsage {
            guard preview.consent == .yes else { return false }
            previewUsage = (.yes, UUID())
            return true
        }
        let ok = usageData.resetNumber()
        usageInstallNumber = usageData.installNumber
        return ok
    }

    /// Setup's added step calls this on entry. Its buttons call setUsageConsent with source .setup.
    func reachedUsageQuestionInSetup() { recordUsage(.setupStepReached(.usageData)) }

    /// An unanswered sheet may be closed for this launch. Boot asks again next time.
    func dismissUsageQuestion() { usageQuestionPending = false }

    func offerUsageQuestionOnThisLaunch() {
        guard !onboard, usageData.shouldAsk else { return }
        Task { [weak self] in
            // Give the separate crash report question priority. Wait for a clear window.
            try? await Task.sleep(for: .seconds(1.5))
            while let self, !self.onboard, self.usageData.shouldAsk, !Task.isCancelled {
                if self.sheet == nil && self.settingsSheet == nil && self.confirmation == nil && self.settingsConfirmation == nil &&
                    !self.gameRunning && !self.hasPendingLaunch && !self.installingUpdate {
                    self.usageQuestionPending = true
                    self.sheet = .usageQuestion
                    return
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    // MARK: Model events

    func recordUsage(_ event: UsageEvent) { if booted && !snapshot { usageData.record(event) } }

    func recordUsageGameStart(engine: GameEngine, renderer: GameRenderer) {
        usageGameStart = nil
        guard booted, !snapshot, let install = usageData.installNumber else { return }
        let mode = UsageDisplayMode(rawValue: settings.fullscreen) ?? .unknown
        usageData.record(.gameStarted(engine: engine, renderer: renderer, mode: mode, scale: settings.renderScale))
        usageGameStart = (gameStartedAt, install)
    }

    func recordUsageGameEnd(code: Int32, at end: Date) {
        defer { usageGameStart = nil }
        guard let start = usageGameStart, start.install == usageData.installNumber else { return }
        let reports = code == 0 || code == 1 ? [] : CrashReports.all(in: home, since: start.time)
        if let how = UsageGameEnd.observed(code: code, requestedQuit: quittingGame, reports: reports) {
            recordUsage(.gameEnded(how, minutes: max(0, Int(end.timeIntervalSince(start.time) / 60))))
        }
    }

    func recordUsageSetupStep(_ step: SetupStep) {
        let usageStep: UsageSetupStep = switch step {
        case .welcome: .welcome
        case .data: .gameData
        case .key: .cdKey
        case .shaders: .shaders
        case .yours: .yours
        case .crash: .crashReports
        case .usage: .usageData
        case .ready: .ready
        }
        recordUsage(.setupStepReached(usageStep))
    }

    func recordUsageNavigation() {
        guard !onboard else { return }
        let screen: UsageScreen = switch page {
        case .home: .home
        case .servers: serverScope == .hosting ? .serversHosting : .servers
        case .library: .library
        case .configs: .configs
        }
        recordUsage(.screenOpened(screen))
        if page == .configs, !discover.landsOnHome { recordUsageConfigsPage() }
    }

    func recordUsageConfigsPage() {
        guard !onboard, page == .configs else { return }
        let screen: UsageScreen = switch discover.page {
        case .home: .configsSetups
        case .saved: .configsSaved
        case .player: .configsPlayerSetup
        case .checker: .configsChecker
        case nil:
            switch studio.tab {
            case .settings: .configsSettings
            case .binds: .configsBinds
            case .checkup: .configsCheckup
            case .history: .configsHistory
            }
        }
        recordUsage(.screenOpened(screen))
    }

    func recordUsageSettingsPane() {
        let screen: UsageScreen = switch settingsPane {
        case .general: .settingsGeneral
        case .display: .settingsDisplay
        case .gameData: .settingsData
        case .privacy: .settingsPrivacy
        case .updates: .settingsUpdates
        case .advanced: .settingsAdvanced
        case .about: .settingsAbout
        }
        recordUsage(.screenOpened(screen))
    }

    func recordUsageUpdate(from: String, to: String, installed: Bool) {
        guard let from = UsageVersion(from), let to = UsageVersion(to) else { return }
        recordUsage(installed ? .updateInstalled(from, to) : .updateOffered(from, to))
    }

    static func usageJoinSource(address: String, pendingAddress: String?, pendingSource: UsageJoinSource?) -> UsageJoinSource {
        address == pendingAddress ? pendingSource ?? .typedAddress : .typedAddress
    }
}

/// What the usage data question says, in Setup, the one-time sheet, See What’s Sent and Settings ›
/// Privacy (UsageData, SetupUsage, SheetUsageAsk, SheetUsagePreview and SetPrivacyUsage boards).
enum UsageDataQuestion {
    static let rows: [(symbol: String, title: String, detail: String)] = [
        ("doc.text", "What is sent", "That you opened the launcher, which screens you open and what you use there, such as setup steps, the games you start (game version and graphics settings) and how long they ran, asking the game to join a server, the player setups you view or import, and the settings you change, with the choice you pick from a set list. With the time to the minute, the app’s version, your macOS version, your Mac’s model and chip, your display, your system language and the country you connect from."),
        ("lock", "What never is", "Your name, your CD key, server names and addresses, chat, what is in your configs, file paths, or anything you type. Your network address is used once to work out your country and continent, then discarded; nothing finer than the country is kept."),
        ("globe", "Where it goes", "To PostHog, a service hosted in the EU, where only this project can see it. A random install number made on this Mac links your sessions; turning this off forgets it."),
    ]
    static let changeLater = CrashReportsQuestion.changeLater
    static let setupTitle = "Send usage data?"
    static let setupDetail = "CoD2 Unbound can send the project a record of how the app is used: which screens and features, never who you are. It shows what to fix first, and lets Player Setups show the setups players use most. The choice is yours."
    static let sheetTitle = "Send usage data to CoD2 Unbound?"
    static let sheetDetail = "New in this version. The launcher can send the project a record of how it is used, so the parts players rely on get fixed first and Player Setups can show the setups players use most. The choice is yours."
    static let send = "Send Usage Data"
    static let dontSend = "Don’t Send"
    /// The bars of a chart, the boards' `bars`.
    static let symbol = "chart.bar"

    // See What’s Sent
    static let previewTitle = "What is sent"
    static let previewDetail = "What the launcher records, and one real example as it would leave this Mac."
    static let exampleCaption = "Example: you joined a server from the list"
    static let previewNote = "The install number is made at random on this Mac. It links your sessions together, not to you, and is forgotten when you turn usage data off. The server’s name and address are not in it."
    static func group(_ group: UsagePreviewGroup) -> (title: String, detail: String) {
        switch group {
        case .screens: ("Screens", "Which screens you open, such as Servers or Settings › Display. Opening the launcher is counted at most once a day.")
        case .setup: ("Setup", "Each step you reach or skip after you say yes, and whether your game files came from Steam or your own copy.")
        case .games: ("Games", "Each game you start, with its game version (CoD2x or Original 1.3), graphics (Metal or OpenGL), display mode and render scale. When the launcher can tell, how the game ended (closed normally, crashed or froze) and how many minutes it ran.")
        case .servers: ("Servers", "Refreshing the list, and which tab: All, Favorites or Recent. Asking the game to join a server: how (from the list, a favorite, a link or a typed address) and its game version when known, never which server.")
        case .playerSetups: ("Player setups", "The public catalogue ID of each player setup you view, try or import, such as “harrow”. That you made a loadout, checked a config, or shared or withdrew a setup, nothing more.")
        case .hosting: ("Hosting", "Starting a server: for your network or the internet, and its game version.")
        case .updates: ("Updates", "Each update offered and installed, with the version you had and the new one.")
        case .settings: ("Settings", "Which settings you change, by name. For a setting with a set list of choices, such as a switch, the game version, appearance, display mode, graphics, render scale or texture sharpness, also the choice you pick. Never what you type into a field.")
        case .problems: ("Problems", "Errors the launcher shows you, by kind, such as “the game couldn’t start”.")
        case .macAndCountry: ("This Mac, your country", "The app’s version, your macOS version, your Mac’s model and chip, your display’s size and refresh rate, your system language, and the country and continent your connection comes from (worked out from your network address, which is then discarded).")
        case .everyEvent: ("With every event", "The time, to the minute; a random number for the event; and the install number. When you say yes, also where: in setup, in the question or in Settings. Each event also says it comes from the launcher and asks PostHog not to build a person profile from it.")
        case .never: ("Never", "Your name, your CD key, server names and addresses, chat, what is in your configs, file paths, or anything you type.")
        }
    }

    // Settings › Privacy
    static let switchTitle = "Send usage data to CoD2 Unbound"
    static let switchDetail = "Which screens and features you use, how long games ran and the choices you pick, with your Mac’s model, versions and country, to PostHog in the EU. Never your name, CD key, servers or configs."
    static let numberTitle = "Install number"
    static let numberDetail = "Made at random on this Mac, it links your usage data from one session to the next. Reset starts a new one; turning usage data off forgets it."
    static let reset = "Reset Number"
    static let contact = (who: "PostHog, in the EU", when: "While you use the launcher, only if usage data is on. What you open and use, never who you are.")
}

// MARK: - Setup links from the website

@MainActor extension LauncherModel {
    /// The catalogue ID in `cod2unbound://setup/<id>`, or nil. Only the host `setup` and exactly one
    /// path part of lower-case letters, digits and hyphens (at most 64, not starting with a hyphen);
    /// never a user, password, port, query or fragment.
    nonisolated static func setupLinkID(_ url: URL) -> String? {
        guard url.scheme?.lowercased() == "cod2unbound",
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              parts.percentEncodedHost?.lowercased() == "setup",
              parts.percentEncodedUser == nil, parts.percentEncodedPassword == nil, parts.port == nil,
              parts.percentEncodedQuery == nil, parts.percentEncodedFragment == nil else { return nil }
        var path = parts.percentEncodedPath
        guard path.hasPrefix("/") else { return nil }
        path.removeFirst()
        if path.hasSuffix("/") { path.removeLast() }
        guard path.range(of: #"\A[a-z0-9][a-z0-9-]{0,63}\z"#, options: .regularExpression) != nil else { return nil }
        return path
    }
    /// True for any `cod2unbound:` link, valid or not: none of them is a server address.
    nonisolated static func isSetupLinkScheme(_ url: URL) -> Bool { url.scheme?.lowercased() == "cod2unbound" }

    /// A setup link from the website: Configs › Player Setups at that setup, once the verified
    /// catalogue is in. It never imports anything; Try This Setup and its review do that. During
    /// setup the link waits until setup is finished.
    func receiveSetupLink(_ url: URL) {
        showMainWindow()
        guard let id = Self.setupLinkID(url) else {
            notice = "This link isn’t a player setup CoD2 Unbound can open."
            return
        }
        if onboard { pendingSetupLink = id; return }
        openSetupLink(id)
    }
    func openSetupLink(_ id: String) {
        pendingSetupLink = nil
        navigate(to: .configs)
        discover.query = ""
        Task { [weak self] in
            guard let self else { return }
            await self.discover.loadIfNeeded()
            // Configs' own page may have started the load already: wait for it to finish.
            for _ in 0..<600 {
                guard case .loading = self.discover.state else { break }
                try? await Task.sleep(for: .milliseconds(100))
            }
            self.showSetupFromLink(id)
        }
    }
    /// After the catalogue load: the setup's page, or the list with a plain message.
    func showSetupFromLink(_ id: String) {
        switch discover.state {
        case .loaded:
            if discover.entry(id) != nil {
                discover.open(.player(id))
            } else {
                discover.openFront()
                notice = "That setup isn’t in Player Setups. It may have been renamed or taken down."
            }
        case .unavailable, .failed:
            discover.openFront()
            notice = "Player Setups couldn’t be loaded, so the setup from the link can’t be shown. Try the link again later."
        case .idle, .loading:
            discover.openFront()
        }
    }
}
