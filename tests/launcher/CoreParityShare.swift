import Foundation

enum ShareParity {
    static func build(catalogURL: URL, scratch: URL) async throws -> [String: Any] {
        let catalog = ConfigImportCatalogAdapter(catalog: try ConfigCatalog(data: Data(contentsOf: catalogURL)))
        let inputs = [ConfigShareTests.loadout, "", "seta sensitivity 2\nseta name Wren\n"]
        var preparedRows: [[String: Any]] = []
        for input in inputs {
            let prepared = try await ConfigShare.prepare(Data(input.utf8), catalog: catalog)
            let request = ConfigShare.requestBody(prepared, name: "Fixture", dialect: .cod2x, app: "0.4.1-dev")
            preparedRows.append([
                "input": input, "bytes": prepared.bytes.base64EncodedString(), "settings": prepared.settings,
                "binds": prepared.binds, "lines": prepared.lines, "reasons": prepared.reasons, "digest": prepared.digest,
                "problem": prepared.problem.map(String.init(describing:)) as Any? ?? NSNull(),
                "body": try JSONSerialization.jsonObject(with: ConfigShareClient.encode(request)),
                "leftOut": prepared.leftOut.map { ["shown": $0.shown, "reasons": $0.reasons, "because": $0.because] }
            ])
        }
        let names = ["  morrow  ", "Sgt   Morrow", "[CLAN] ~morrow* (2)", "Мороз 東京", "Cafe\u{0301}", "", "me@example", "a\tb"]
            + ["#", "<", ">", "`", "/", "\\", ":", "\""].map { "a\($0)b" }
            + [String(repeating: "a", count: 32), String(repeating: "a", count: 33)]
        let nameRows = names.map { input in
            ["input": input, "name": ConfigShareName.valid(input) as Any? ?? NSNull(),
             "problem": ConfigShareName.problem(input) as Any? ?? NSNull()]
        }
        let token = String(repeating: "a", count: 64)
        let url = ConfigShareClient.pulls + "128"
        let shareInputs: [(Int, String)] = [
            (201, "{\"request\":128,\"url\":\"\(url)\",\"id\":\"wren\",\"token\":\"\(token)\"}"),
            (201, "{\"request\":129,\"url\":\"\(url)\",\"id\":\"wren\",\"token\":\"\(token)\"}"),
            (201, "{\"request\":128,\"url\":\"\(url)\",\"id\":\"wren\",\"token\":\"short\"}"),
            (201, "{\"request\":128,\"url\":\"https://example.invalid/pull/128\",\"id\":\"wren\",\"token\":\"\(token)\"}"),
            (409, "{\"error\":\"already\"}"), (409, "<html>"),
            (429, "{\"error\":\"limit\",\"scope\":\"day\"}"), (429, "{\"error\":\"limit\",\"scope\":\"network\"}"),
            (413, "{\"error\":\"invalid\",\"field\":\"config\"}")
        ] + [400, 403, 404, 405, 500, 503, 200, 302].map { ($0, "{\"error\":\"invalid\",\"field\":\"name\"}") }
        let shareRows = shareInputs.map { status, body -> [String: Any] in
            let outcome: String
            switch ConfigShareClient.shareReply(status: status, body: Data(body.utf8)) {
            case .sent: outcome = "sent"
            case .already: outcome = "already"
            case .limit(let everyone): outcome = everyone ? "day" : "network"
            case .tooLarge: outcome = "tooLarge"
            case .unreachable: outcome = "unreachable"
            case .cancelled: outcome = "cancelled"
            }
            return ["status": status, "body": body, "outcome": outcome]
        }
        let withdrawRows = [
            (200, "{\"state\":\"withdrawn\"}"), (200, "{\"state\":\"removal\",\"url\":\"\(url)\"}"),
            (200, "{\"state\":\"removal\",\"url\":\"https://example.invalid/pull/128\"}"),
            (403, "{\"error\":\"forbidden\"}"), (503, "{\"error\":\"unavailable\"}")
        ].map { status, body -> [String: Any] in
            let outcome: String
            switch ConfigShareClient.withdrawReply(status: status, body: Data(body.utf8)) {
            case .withdrawn: outcome = "withdrawn"
            case .removal: outcome = "removal"
            case .forbidden: outcome = "forbidden"
            case .unreachable: outcome = "unreachable"
            case .cancelled: outcome = "cancelled"
            }
            return ["status": status, "body": body, "outcome": outcome]
        }

        let home = scratch.appendingPathComponent("parity-share")
        defer { try? FileManager.default.removeItem(at: home) }
        var saved = ConfigShareRecordFile()
        saved.remember(.init(request: 128, url: URL(string: url)!, token: token, sent: "2026-11-21", sha256: String(repeating: "b", count: 64),
                             name: "Wren"), loadout: "Main")
        try saved.save(home: home)
        let record = try Data(contentsOf: ConfigShareRecordFile.url(home: home))
        ConfigShareTests.serviceRules()
        ConfigShareTests.names()
        ConfigShareTests.replies()
        ConfigShareTests.credit()
        return ["prepared": preparedRows, "names": nameRows, "shares": shareRows, "withdrawals": withdrawRows,
                "records": record.base64EncodedString()]
    }
}
