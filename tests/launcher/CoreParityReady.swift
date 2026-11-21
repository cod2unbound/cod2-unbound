import Foundation

enum ReadyParity {
    static func build(root: URL) throws -> [[String: Any]] {
        let resources = root.appendingPathComponent("launcher/Resources")
        let catalog = try ConfigCatalog(data: Data(contentsOf: resources.appendingPathComponent("ConfigCatalog.json")))
        let rules = try ConfigRules(data: Data(contentsOf: resources.appendingPathComponent("ConfigRulesets.json")))
        let checker = ConfigReadyCheck(catalog: catalog, rules: rules)
        let base = ["protocol":"120", "fs_game":"", "g_competitive":"1", "sv_fps":"20", "sv_maxRate":"25000"]
        let text = """
        seta com_maxfps "333"
        seta rate "25000"
        seta snaps "40"
        seta cl_maxpackets "125"
        seta sc_enable "0"
        seta fx_sort "1"
        seta mss_q3fs "1"
        seta m_filter "0"
        seta cl_mouseAccel "0"
        seta r_picmip "1"
        seta r_picmip_manual "1"
        """ + "\n"
        typealias Case = (String, [String: String], [String], String, [String], ConfigCatalogMode, Int?, Bool)
        var cases: [Case] = [
            ("competitive", base, [], text, [], .cod2x, 10, true),
            ("compatible", base, [], text.replacingOccurrences(of: "333", with: "250"), [], .cod2x, 1, true),
            ("locked", base, [], text, ["COM_MAXFPS"], .cod2x, 1, true),
            ("stale", base, [], text, [], .cod2x, 61, true),
            ("future", base, [], text, [], .cod2x, -61, true),
            ("confirmed", base, [], text, [], .cod2x, 300, false),
            ("never observed", base, [], text, [], .cod2x, nil, false),
            ("wrong client", base, [], text, [], .stock, 1, true),
            ("different replies", base, ["g_competitive"], text, [], .cod2x, 1, true),
            ("wait bind", base, [], text + "bind MOUSE5 \"+attack; wait; -attack\"\n", [], .cod2x, 2, true),
            ("dynamic bind", base, [], text + "bind MOUSE5 \"vstr script\"\n", [], .cod2x, 2, true),
            ("missing value", base, [], "seta com_maxfps 250\n", [], .cod2x, 2, true),
        ]
        for (name, fields) in [("no protocol", [:]), ("no competitive flag", ["protocol":"120", "fs_game":""]),
            ("unknown mod", ["protocol":"118", "fs_game":"pam"]), ("unknown zpam", ["protocol":"120", "fs_game":"zpam", "_zpam":"4.09", "g_competitive":"1"]),
            ("contradictory mod", ["protocol":"120", "fs_game":"zpam408", "_zpam":"3.36"]),
            ("extra league", ["protocol":"118", "fs_game":"", "sv_ruleset":"invented"]),
            ("invalid rate", base.merging(["sv_maxRate":"NaN"]) { _, new in new }),
            ("stock", ["protocol":"118", "fs_game":""]), ("zpam3", ["protocol":"118", "fs_game":"zpam336"]),
            ("zpam4", ["protocol":"120", "fs_game":"zpam408", "g_competitive":"1"])] {
            cases.append((name, fields, [], text, [], fields["protocol"] == "118" ? .stock : .cod2x, 1, true))
        }
        return cases.map { name, fields, conflicts, text, locks, mode, age, requireFresh in
            let bytes = Data(text.utf8)
            let document = ConfigDocument(data: bytes, dialect: mode == .cod2x ? .cod2x : .stock)
            var metadata = ConfigStudioMetadata(displayName: "Fixture"); metadata.locks = Set(locks)
            let profile = ConfigProfileRead(profile: .init(id: "Fixture", displayName: "Fixture", isActive: true),
                document: document, metadata: metadata, version: .init(config: bytes, metadata: nil))
            let server = GameServer(address: "localhost", fields: fields, ping: 12, players: [],
                observedAt: age.map { Date(timeIntervalSinceNow: -Double($0)) }, queryConflicts: conflicts)
            let report = checker.evaluate(server: server, profile: profile, mode: mode, requireFresh: requireFresh)
            return ["name": name, "fields": fields, "conflicts": conflicts, "input": bytes.base64EncodedString(), "locks": locks,
                    "mode": mode.rawValue, "age": age as Any? ?? NSNull(), "requireFresh": requireFresh, "summary": report.summary,
                    "status": report.status.rawValue, "ids": report.rulesetIDs, "fixes": report.fixes, "facts": report.facts,
                    "details": report.details.map { ["setting": $0.setting, "status": $0.status.rawValue, "reason": $0.reason] },
                    "limitations": report.limitations]
        }
    }
}
