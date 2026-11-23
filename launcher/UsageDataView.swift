import SwiftUI
import AppKit

// Usage data's sheets (Sheets board kinds usageAsk and usagePreview): the question asked once after
// the update that brings it, and See What’s Sent…, which Setup, the question and Settings › Privacy
// open. The words are in UsageDataQuestion (ModelUsage.swift); every answer goes through the model.

/// Asked once of people set up before Setup had the question (SheetUsageAsk board), the way Send crash
/// reports? is: Escape is “Don’t Send”, and Return presses nothing because neither answer is the default.
/// Closed without an answer (the app quit with it open), it is asked again at the next start.
struct UsageQuestionSheet: View {
    @ObservedObject var model: LauncherModel
    @Environment(\.palette) private var palette
    @State private var previewShown = false
    var body: some View {
        SheetScaffold(icon: .symbol(UsageDataQuestion.symbol), title: UsageDataQuestion.sheetTitle, message: UsageDataQuestion.sheetDetail,
                      actions: [.cancel(UsageDataQuestion.dontSend) { model.setUsageConsent(send: false, source: .sheet); model.dismissSheet() },
                                SheetAction(title: UsageDataQuestion.send, role: .primary, returnKey: false) {
                                    model.setUsageConsent(send: true, source: .sheet); model.dismissSheet()
                                }]) {
            VStack(alignment: .leading, spacing: 16) {
                FormGroup {
                    ForEach(Array(UsageDataQuestion.rows.enumerated()), id: \.offset) { _, item in
                        QuestionFactRow(symbol: item.symbol, title: item.title, detail: item.detail)
                    }
                }
                Text(UsageDataQuestion.changeLater).font(.system(size: 12)).foregroundStyle(palette.tertiary).padding(.leading, 6)
            }
        } note: {
            Button("See What’s Sent…") { previewShown = true }.quietAction().padding(.horizontal, -6)
                .help("What usage data holds, and one event exactly as it is sent")
        }
        .sheet(isPresented: $previewShown) { UsagePreviewSheet(model: model) { previewShown = false }.sheetShapeAudit() }
        .onDisappear { model.dismissUsageQuestion() }
    }
}

/// One fact on a question sheet: its symbol tile, a title and a line of detail (as Send crash reports? shows them).
struct QuestionFactRow: View {
    var symbol: String
    var title: String
    var detail: String
    @Environment(\.palette) private var palette
    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol).font(.system(size: 14, weight: .medium)).foregroundStyle(palette.accent)
                .frame(width: 30, height: 30)
                .background(palette.selection, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 14, weight: .semibold)).foregroundStyle(palette.text)
                Text(detail).font(.system(size: 12)).foregroundStyle(palette.tertiary).lineSpacing(1.5).fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 16).padding(.vertical, 11)
        .overlay(alignment: .top) { Rectangle().fill(palette.separator).frame(height: 1) }
        .accessibilityElement(children: .combine).accessibilityAddTraits(.isStaticText)
    }
}

/// See What’s Sent… (SheetUsagePreview board): what is recorded, then one event as the
/// sender encodes it, with this Mac's facts and its install number (all zeros before a yes).
struct UsagePreviewSheet: View {
    @ObservedObject var model: LauncherModel
    var done: () -> Void
    @Environment(\.palette) private var palette
    @State private var copied = false
    var body: some View {
        let sample = model.usageSampleJSON ?? ""
        SheetScaffold(icon: .symbol("doc.text"), title: UsageDataQuestion.previewTitle, message: UsageDataQuestion.previewDetail, width: 600, scrollHeight: 500,
                      actions: [SheetAction(title: copied ? "Copied" : "Copy", role: .leading, enabled: !sample.isEmpty) { copy(sample) },
                                SheetAction(title: "Done", role: .primary, initialFocus: true) { done() }]) {
            VStack(alignment: .leading, spacing: 14) {
                FormGroup {
                    ForEach(model.usagePreviewGroups) { group in
                        let words = UsageDataQuestion.group(group)
                        HStack(alignment: .top, spacing: 12) {
                            Text(words.title).font(.system(size: 13, weight: .semibold)).foregroundStyle(palette.text)
                                .frame(width: 150, alignment: .leading)
                            Text(words.detail).font(.system(size: 12)).foregroundStyle(palette.tertiary).lineSpacing(1.5)
                                .frame(maxWidth: .infinity, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                        }
                        .padding(.horizontal, 16).padding(.vertical, 10)
                        .overlay(alignment: .top) { Rectangle().fill(palette.separator).frame(height: 1) }
                        // VoiceOver reads each group as one line, in the order shown.
                        .accessibilityElement(children: .combine).accessibilityAddTraits(.isStaticText)
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text(UsageDataQuestion.exampleCaption).font(.system(size: 12, weight: .semibold)).foregroundStyle(palette.secondary)
                        .padding(.leading, 4).accessibilityAddTraits(.isHeader)
                    Text(sample).font(.typewriter(12)).foregroundStyle(palette.secondary).lineSpacing(3)
                        .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 14).padding(.vertical, 12)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel("The exact text of one event")
                        .accessibilityValue(sample)
                        .accessibilityAddTraits(.isStaticText)
                        .background(palette.well, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(palette.wellStroke, lineWidth: 1))
                        .shapeAudit(.container, .rounded(12), "event well")
                }
                Text(UsageDataQuestion.previewNote).font(.system(size: 12)).foregroundStyle(palette.tertiary)
                    .fixedSize(horizontal: false, vertical: true).padding(.leading, 6)
                Link(PrivacyContacts.noticeTitle, destination: PrivacyContacts.noticeURL)
                    .font(.system(size: 12)).foregroundStyle(palette.link).padding(.leading, 6).padding(.bottom, 2)
            }
        }
    }
    private func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text + "\n", forType: .string)
        copied = true
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [.announcement: "Copied."])
    }
}

// MARK: Setup › Usage data (SetupUsage board)
extension SetupView {
    var usagePanel: some View {
        panel {
            group {
                ForEach(Array(UsageDataQuestion.rows.enumerated()), id: \.offset) { index, item in
                    if index > 0 { divider() }
                    row(item.symbol, item.title, item.detail) { EmptyView() }
                }
            }
            HStack(spacing: 8) {
                note(UsageDataQuestion.changeLater)
                Spacer(minLength: 8)
                Button("See What’s Sent…") { model.present(.usagePreview) }.quietAction()
                    .help("What usage data holds, and one event exactly as it is sent")
            }
            .padding(.leading, 18).padding(.trailing, 12).padding(.bottom, 8)
        }
    }
}
