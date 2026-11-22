import Foundation

enum CheckupParity {
    static func build(root: URL, scratch: URL) throws -> [[String: Any]] {
        let resources = root.appendingPathComponent("launcher/Resources")
        let optimizer = try ConfigOptimizer(data: Data(contentsOf: resources.appendingPathComponent("ConfigOptimizer.json")))
        let model = try ConfigCheckup(catalog: ConfigCatalog(data: Data(contentsOf: resources.appendingPathComponent("ConfigCatalog.json"))),
                                     rules: ConfigRules(data: Data(contentsOf: resources.appendingPathComponent("ConfigRulesets.json"))),
                                     optimizer: optimizer)
        let home = scratch.appendingPathComponent("parity-checkup")
        defer { try? FileManager.default.removeItem(at: home) }
        let store = try ConfigProfileStore(homeURL: home, dialect: .cod2x)
        let text = "// keep bytes\r\nseta com_maxfps \"333\"\r\nseta m_filter \"1\"\r\nseta cl_mouseAccel \"2\"\r\nseta rate \"5000\"\r\nseta snaps \"20\"\r\nseta cl_maxpackets \"30\"\r\nseta r_picmip \"3\"\r\nseta r_drawSun \"1\"\r\nseta cg_fov \"90\"\r\nbind F1 \"wait; +attack\"\r\n"
        typealias Case = (String, String, [String], ConfigTuneFor, ConfigCatalogMode, [String], [String], Bool)
        var items: [Case] = [
            ("competitive", text, ["zpam-4.08"], .competitive, .cod2x, ["CL_MOUSEACCEL"], [], true),
            ("public", text, ["public-cod2x"], .balanced, .cod2x, ["CL_MOUSEACCEL"], [], true),
            ("stock rules", text, ["stock-1.3"], .balanced, .stock, [], [], false),
            ("unknown", text, ["pam"], .competitive, .cod2x, [], [], false),
            ("unresolved", text + "exec missing.cfg\n", ["public-cod2x"], .balanced, .cod2x, [], [], false),
            ("texture locked", text + "seta r_picmip_manual \"0\"\n", ["public-cod2x"], .bestLooking, .cod2x, ["R_PICMIP_MANUAL"], [], false),
            ("dismissed", text, ["public-cod2x"], .balanced, .cod2x, [], ["unfiltered-aim"], false),
        ]
        for rule in optimizer.rules {
            let initial = rule.setting == "com_maxfps" ? "333" : rule.setting == "r_picmip" ? "3" :
                rule.setting == "cl_mouseAccel" ? "2" : rule.setting == "m_filter" ? "1" : "10"
            items.append((rule.id, "seta \(rule.setting) \"\(initial)\"\n",
                          [rule.setting == "com_maxfps" ? "cod2x-competitive" : "public-cod2x"],
                          rule.tuneFor[0], .cod2x, [], [], true))
        }
        return try items.enumerated().map { index, item -> [String: Any] in
            let (name, text, ids, tune, mode, locks, dismissed, apply) = item
            let bytes = Data(text.utf8)
            var metadata = ConfigStudioMetadata(displayName: "Checkup"); metadata.locks = Set(locks)
            let profile = try store.create(id: "Profile\(index)", displayName: "Checkup", document: .init(data: bytes, dialect: .cod2x), metadata: metadata)
            let report = model.evaluate(profile: profile, rulesetIDs: ids, tuneFor: tune, targetMode: mode,
                                        measurements: .init(frameTimeP99MS: 8, mousePollingHz: -1, pingMS: 42, source: "synthetic"),
                                        dismissedSuggestionIDs: Set(dismissed))
            var applied: String?, undone: String?
            if apply && !report.suggestions.isEmpty {
                let receipt = try model.apply(report: report, suggestionIDs: Set(report.suggestions.map(\.id)), store: store)
                applied = try store.read(id: profile.profile.id).data.base64EncodedString()
                _ = try model.undo(receipt: receipt, rulesetIDs: ids, store: store, targetMode: mode)
                undone = try store.read(id: profile.profile.id).data.base64EncodedString()
            }
            return ["id": name, "input": bytes.base64EncodedString(), "ids": ids, "tune": tune.rawValue, "mode": mode.rawValue,
                    "locks": locks, "dismissed": dismissed, "unresolved": report.unresolvedScripts, "limitations": report.limitations,
                    "policy": report.policy.map { ["name": $0.setting, "status": $0.status.rawValue, "reason": $0.reason] },
                    "suggestions": report.suggestions.map { ["id": $0.id, "area": $0.area.rawValue, "title": $0.title, "why": $0.why, "notice": $0.notice,
                        "changes": $0.changes.map { ["name": $0.setting, "old": $0.oldValue as Any? ?? NSNull(), "new": $0.newValue] }] },
                    "dials": report.dials.map { ["area": $0.area.rawValue, "value": $0.value as Any? ?? NSNull(), "unit": $0.unit as Any? ?? NSNull()] },
                    "applied": applied as Any? ?? NSNull(), "undone": undone as Any? ?? NSNull()]
        }
    }
}
