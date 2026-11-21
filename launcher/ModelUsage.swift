import Foundation

@MainActor extension LauncherModel {
    // MARK: Privacy and setup presentation

    var usageConsent: UsageConsent { usageData.consent }
    var usageAvailable: Bool { usageData.available }
    var shouldAskUsageInSetup: Bool { onboard && usageData.shouldAsk }
    var usageSampleJSON: String? { usageData.sampleEventJSON }
    var usagePreviewGroups: [UsagePreviewGroup] { UsagePreviewGroup.allCases }

    @discardableResult func setUsageConsent(send: Bool, source: UsageConsentSource) -> Bool {
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
                    return
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    // MARK: Model events

    func recordUsage(_ event: UsageEvent) { if booted && !snapshot { usageData.record(event) } }

    func recordUsageSetupStep(_ step: SetupStep) {
        let usageStep: UsageSetupStep = switch step {
        case .welcome: .welcome
        case .data: .gameData
        case .key: .cdKey
        case .shaders: .shaders
        case .yours: .yours
        case .crash: .crashReports
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
