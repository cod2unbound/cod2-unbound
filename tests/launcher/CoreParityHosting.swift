import Foundation

enum HostingParity {
    static func build() throws -> [String: Any] {
        var open = HostedServer.new(id: "one", name: "Friday \"Rifles\"; quit // night")
        open.joinPassword = "let me in"
        open.adminPassword = "admin\";quit"
        open.maps = ["mp_toujane", "../etc", "mp_carentan; quit", "mp_burgundy"]
        open.players = 500
        open.friendlyFire = true
        var match = HostedServer.new(id: "two", name: "Scrim Room", engine: .original)
        match.rules = .zpam
        match.reach = .listed
        match.matchMode = "sd:comp_mr15"
        match.maps = ["mp_dawnville", "mp_matmata"]
        match.port = 28962
        match.joinPassword = "scrimnight"
        match.adminPassword = "secretword"
        let template = ["// zPAM", "set g_gametype \"sd\"", "seta sv_wwwBaseURL \"http://cod2x.me/zpam\"",
                        "set pam_mode \"comp\"", "set scr_sd_timelimit 0", "map mp_toujane_fix", ""].joined(separator: "\r\n")
        let digest = String(repeating: "ab", count: 32)
        let base = "https://github.com/eyza-cod2/zpam3/releases/download"
        func asset(_ name: String, _ url: String, _ digest: String?) -> [String: Any] {
            var result: [String: Any] = ["name": name, "browser_download_url": url, "size": 1000]
            if let digest { result["digest"] = digest }
            return result
        }
        let listing = try JSONSerialization.data(withJSONObject: [
            ["draft": true, "assets": [asset("zpam499.zip", "\(base)/4.99/zpam499.zip", "sha256:\(digest)")]],
            ["prerelease": true, "assets": [asset("zpam498.zip", "\(base)/4.98/zpam498.zip", "sha256:\(digest)")]],
            ["assets": [asset("zpam413.zip", "https://example.invalid/zpam413.zip", "sha256:\(digest)"),
                        asset("zpam413_test.zip", "\(base)/4.13/zpam413_test.zip", "sha256:\(digest)")]],
            ["assets": [asset("zpam412.zip", "\(base)/4.12/zpam412.zip", nil)]],
            ["assets": [asset("zpam411.zip", "\(base)/4.11/zpam411.zip", "sha256:\(digest.uppercased())")]],
            ["assets": [asset("zpam336.zip", "\(base)/3.36/zpam336.zip", "sha256:\(digest)")]]
        ], options: [.sortedKeys, .withoutEscapingSlashes])
        let release = ZPAMRelease.newest(.three, in: listing)!
        let servers = [open, match]
        let configs: [[String: Any]] = try servers.map { server in
            let raw = try JSONEncoder().encode(server)
            let record = try JSONSerialization.jsonObject(with: raw)
            let tpl = server.rules == .zpam ? template : nil
            return ["server": record, "template": tpl as Any? ?? NSNull(),
                    "config": tpl.map { HostingConfig.serverConfig(server, zpamTemplate: $0) } ?? HostingConfig.serverConfig(server),
                    "fileName": HostingConfig.fileName(server),
                    "export": HostingConfig.export(server, zpamTemplate: tpl, zpam: release, passwords: false, matchSettings: true),
                    "publicExport": HostingConfig.export(server, zpamTemplate: tpl, zpam: release, passwords: true, matchSettings: false),
                    "otherPort": HostingConfig.otherPort(for: server, free: { $0 != 28961 })]
        }
        let statuses = [
            "statusResponse\n\\g_gametype\\tdm\\mapname\\mp_toujane\\sv_maxclients\\12\\sv_hostname\\Friday\n14 4 \"Sample Player\"\n9 21 \"Second \"Sample\"\n",
            "statusResponse\n\\_match_info\\Round 0 | MR15 Ready-up\\mapname\\mp_toujane_fix\n",
            "statusResponse\n\\_match_round\\^2Ready-up\\mapname\\mp_toujane_fix\n",
            "statusResponse\n\\mapname\\first\\MAPNAME\\last\ninvalid\n1 2 \"After a bad row\"\n",
            "print\nBad rcon", ""
        ].map { value -> [String: Any] in
            guard let status = HostedStatus.parse(value) else { return ["input": value, "status": NSNull()] }
            return ["input": value, "status": ["map": status.map, "gameType": status.gameType,
                "maxPlayers": status.maxPlayers, "matchInfo": status.matchInfo as Any? ?? NSNull(),
                "players": status.players.map { ["id": $0.id, "name": $0.name, "score": $0.score, "ping": $0.ping] }]]
        }
        let releases = [ZPAMLine.four, .three].compactMap { line -> [String: Any]? in
            guard let release = ZPAMRelease.newest(line, in: listing) else { return nil }
            return ["line": line.rawValue, "version": release.version, "asset": release.asset,
                    "url": release.url.absoluteString, "bytes": release.bytes, "sha256": release.sha256]
        }
        return ["configs": configs, "status": statuses, "listing": listing.base64EncodedString(), "releases": releases]
    }
}
