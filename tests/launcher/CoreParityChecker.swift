import Foundation

enum CheckerParity {
    static func build(root: URL) async throws -> [[String: Any]] {
        let resources = root.appendingPathComponent("launcher/Resources")
        let catalog = try ConfigCatalog(data: Data(contentsOf: resources.appendingPathComponent("ConfigCatalog.json")))
        let rules = try ConfigRules(data: Data(contentsOf: resources.appendingPathComponent("ConfigRulesets.json")))
        let sample = [
            "// a sample config", "seta com_maxfps \"333\"", "seta sensitivity \"1.4\"", "seta cl_maxpakets \"100\"",
            "seta rate \"25000\"", "seta snaps \"40\"", "seta cg_fov \"90\"", "seta r_vsync \"0\"",
            "seta r_picmip \"3\"", "seta m_pitch \"0.022\"", "seta m_pitch \"0.022\"", "bind MOUSE1 \"+attack\"",
            "bind F9 \"rcon login synthetic-secret\"", "seta cg_fov \"90\"; seta m_rinput \"1\"",
        ].joined(separator: "\r\n") + "\r\n"
        let cases: [(String, String, ConfigDialect, String?)] = [
            ("original", sample, .stock, "zpam-3.36"), ("cod2x", sample, .cod2x, nil),
            ("mixed", "seta snaps 99; set password synthetic-secret\n\t// comment\nseta sensitivity \"5", .stock, nil),
            ("empty", "", .stock, nil),
        ]
        var rows: [[String: Any]] = []
        for (id, text, dialect, policy) in cases {
            let input = Data(text.utf8)
            let report = try await ConfigChecker.check(input, dialect: dialect, ruleset: policy, catalog: catalog, rules: rules)
            let fixed = Set(report.fixable.map(\.id))
            let applied = report.applying(fixed)
            rows.append(["id": id, "input": input.base64EncodedString(), "cod2x": dialect == .cod2x,
                "policy": policy as Any? ?? NSNull(), "commands": report.commands, "fixed": applied.base64EncodedString(),
                "lines": report.lines.map(\.text), "shown": report.shown(applied: fixed).map { ["text": $0.text, "note": $0.note] },
                "findings": report.findings.map { finding in
                    ["id": finding.id, "end": finding.range.upperBound, "line": finding.line, "kind": finding.kind.rawValue,
                     "title": finding.title, "detail": finding.detail, "note": finding.note,
                     "fixLabel": finding.fixLabel as Any? ?? NSNull()]
                }])
        }
        return rows
    }
}
