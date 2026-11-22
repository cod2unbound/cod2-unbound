import Foundation

enum WireParity {
    static func build(root: URL) throws -> [String: Any] {
        func packet(_ text: String) -> Data { WireParser.marker + Data(text.utf8) }
        func read(_ name: String) throws -> Data { try Data(contentsOf: root.appendingPathComponent("tests/launcher/fixtures/" + name + ".bin")) }
        let status: [(String, Data)] = [
            ("recorded-status", try read("status")),
            ("status", packet("statusResponse\n\\sv_hostname\\^1Local\\protocol\\120\\sv_maxClients\\16\\g_gametype\\sd\\mapname\\mp_toujane\\pswrd\\1\n-12 45 \"^2Tester\"\nwrong 19 \"Skip\"\n0 +1 \"^7Other\"\n")),
            ("info", packet("infoResponse\n\\hostname\\^3Info\\clients\\11\\gametype\\tdm\\g_password\\1\\shortversion\\1.4.6.8\\fs_game\\mods/test\n")),
            ("minimal", packet("statusResponse\n\n")),
            ("latin1", packet("statusResponse\n\\sv_hostname\\^1caf") + Data([0xe9]) + Data("\n3 5 \"J".utf8) + Data([0xf6]) + Data("\"\n".utf8)),
            ("legacy-control", packet("statusResponse\n\\hostname\\") + Data([0xff, 0x80, 0x92]) + Data("\n".utf8)),
            ("invalid-marker", Data("statusResponse\n\\x\\y".utf8)),
            ("invalid-header", packet("notStatus\n\\x\\y")),
            ("invalid-fields", packet("statusResponse\n\\x")),
            ("bad-first-field", packet("statusResponse\nx\\y\\z")),
            ("oversized-field", packet("statusResponse\n\\x\\" + String(repeating: "x", count: 2049))),
            ("oversized-key", packet("statusResponse\n\\" + String(repeating: "x", count: 129) + "\\v")),
            ("equal-duplicate", packet("statusResponse\n\\x\\y\\x\\y")),
            ("conflicting-field", packet("statusResponse\n\\x\\y\\x\\different")),
            ("status-crlf", packet(" statusResponse \r\n\\hostname\\one\r\n0 7 \"two\"\r\n")),
            ("player-name-limit", packet("statusResponse\n\\hostname\\demo\n1 2 \"" + String(repeating: "e\u{301}", count: 130) + "\"\n"))
        ]
        let address = "loopback.invalid:28960", ping = 28
        let statusResults = status.map { id, bytes -> [String: Any] in
            let row: [String: Any] = ["id": id, "packet": bytes.base64EncodedString(), "address": address, "ping": ping]
            do { return row.merging(snapshot(try WireParser.status(bytes, address: address, ping: ping))) { a, _ in a } }
            catch { return row.merging(["error": error.localizedDescription]) { a, _ in a } }
        }

        var joined = try WireParser.status(packet("statusResponse\n\\sv_hostname\\Original\\Case\\first\\protocol\\120\n1 29 \"Local\"\n"), address: address, ping: 31)
        let info = try WireParser.status(packet("infoResponse\n\\hostname\\Information\\case\\second\\protocol\\118\\clients\\2\n"), address: address, ping: 7)
        joined.mergeInfo(info)
        let masters: [(String, Data)] = try ["master.cod2x.me-118", "master.cod2x.me-120",
                                             "cod2master.activision.com-118", "cod2master.activision.com-120"].map { ( $0, try read($0)) } +
            [("missing-marker", Data("getserversResponse".utf8)),
             ("synthetic-master", packet("getserversResponse\r\n\0") +
                Data([92, 1, 2, 3, 4, 0x71, 0x20, 92, 0, 1, 2, 3, 1, 2, 92, 239, 1, 2, 3, 1, 2,
                      92, 1, 2, 3, 4, 0x71, 0x20, 92, 5, 6, 7, 8, 0, 0, 92, 5, 6, 7, 8, 1, 2]) +
                Data("\\EOT\0\0\0".utf8) + Data([92, 8, 8, 8, 8, 1, 2])),
             ("truncated-master", packet("getserversResponse") + Data([92, 1, 2, 3, 4, 5]))]
        let masterResults: [[String: Any]] = masters.map { id, bytes in
            ["id": id, "packet": bytes.base64EncodedString(), "addresses": WireParser.master(bytes)]
        }
        let colors: [[String: Any]] = ["Normal", "^1Red^7White", "x^^1y^", "^1^2x^3", "^x^2🕹️", "^２two^²super^⑦seven^⑩ten",
                                       String(repeating: "a", count: 1023) + "^1X"].map { name in
            ["input": name, "plain": QuakeColors.plain(name), "runs": QuakeColors.runs(name).map { ["text": $0.text, "code": $0.code] }]
        }
        let links: [[String: Any]] = [
            "cod2x://connect/LOCALhost:28960/", "COD2X://connect+host.test+password+%22fake%20value%2B%22",
            "cod2x://password%20%22%22%20%2Bconnect%20.a..-:00001", "cod2x://connect%20host%20quit%20x",
            "cod2x://connect%20host%3A0", "cod2x://connect%20host%3A65536", "cod2x://connect%20host%00",
            "cod2x://connect%20[::1]", "cod2x://connect%20host%20password%20%22%3B%22", "cod2x://connect%Q0",
            "cod2x://connect%20host%20password%20a%20password%20b", "cod2x://connect%20host%20password%20%22a%22b",
            "https://connect/host", "cod2x://connect%20host%20password%20%22%C3%A9%22", "cod2x://connect%20host:289600"
        ].map { value in
            if let link = try? LaunchLink(value) {
                return ["input": value, "address": link.address, "password": link.password,
                        "canonical": try! LaunchLink.canonicalAddress(link.address)]
            }
            return ["input": value]
        }
        return ["statuses": statusResults, "merged": snapshot(joined), "masters": masterResults, "colors": colors, "links": links]
    }

    private static func snapshot(_ value: GameServer) -> [String: Any] {
        ["fields": value.fields, "name": value.name, "map": value.map, "gametype": value.gametype,
         "maxPlayers": value.maxPlayers, "playerCount": value.playerCount, "version": value.version,
         "mod": value.mod, "password": value.password, "statusObservation": value.observedAt != nil,
         "players": value.players.map { ["id": $0.id, "score": $0.score, "ping": $0.ping, "name": $0.name] },
         "conflicts": value.queryConflicts as Any? ?? NSNull()]
    }
}
