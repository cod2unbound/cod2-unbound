import Foundation

enum LifecycleParity {
    static func build(root: URL, scratch: URL) throws -> [String: Any] {
        let cases: [(Int32?, Bool, Bool)] = [
            (0, true, true), (1, true, true), (2, true, true), (-1, false, true),
            (2, false, false), (nil, true, false), (nil, true, true), (nil, false, false)
        ]
        let outcomes = cases.map { code, hasLog, hasCrash -> [String: Any] in
            let result = GameExit.outcome(code: code, sessionLog: { hasLog ? URL(fileURLWithPath: "/session") : nil },
                                          crashReport: { hasCrash ? URL(fileURLWithPath: "/crash") : nil })
            return ["code": code as Any? ?? NSNull(), "hasLog": hasLog, "hasCrash": hasCrash,
                    "unexpected": result.unexpected, "report": result.report?.path as Any? ?? NSNull()]
        }
        let home = scratch.appendingPathComponent("parity-lifecycle")
        defer { try? FileManager.default.removeItem(at: home) }
        let stats = home.appendingPathComponent("main/stats")
        try FileManager.default.createDirectory(at: stats, withIntermediateDirectories: true)
        let path = stats.appendingPathComponent("matches.jsonl")
        let freeze = try Data(contentsOf: root.appendingPathComponent("tests/launcher/fixtures/match-freezes.json"))
        let legacy = try Data(contentsOf: root.appendingPathComponent("tests/launcher/fixtures/match-freeze-legacy.json"))
        let row = String(decoding: legacy, as: UTF8.self)
        func variant(_ field: String, _ value: String) -> Data {
            let expression = "\"" + field + "\"\\s*:\\s*[^,}\\n]+"
            let result = row.replacingOccurrences(of: expression, with: "\"\(field)\":\(value)", options: .regularExpression)
            precondition(result != row)
            return Data(result.utf8)
        }
        let inputs: [(String, Data)] = [
            ("current", freeze), ("legacy", legacy), ("partial", Data(legacy.dropLast())),
            ("invalid schema", variant("schemaVersion", "2")),
            ("huge duration", variant("durationSeconds", "1e300")),
            ("negative duration", variant("durationSeconds", "-1")),
            ("overflow", variant("packetsLost", "18446744073709551615")),
            ("inconsistent", variant("capSampleFrames", "1000000")),
            ("out of range", variant("frameTimeMaxMS", "2147483648")),
            ("histogram", variant("frameTimeHistogramLimitMS", "0")),
            ("loss mismatch", variant("packetLossPercent", "90")),
            ("mixed", freeze + Data("bad\n".utf8) + legacy + Data(legacy.dropLast()))
        ]
        let matches = try inputs.map { name, bytes -> [String: Any] in
            try bytes.write(to: path)
            let results = try MatchSummaries.read(homePath: home)
            return ["name": name, "input": bytes.base64EncodedString(),
                    "matches": results.map { ["id": $0.id, "exact": $0.frameQuantilesAreExact,
                        "summary": $0.freezeSummary as Any? ?? NSNull(), "times": $0.freezeTimes as Any? ?? NSNull()] }]
        }
        return ["outcomes": outcomes, "records": matches]
    }
}
