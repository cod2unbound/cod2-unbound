import Foundation

enum SettingsParity {
    static func build(root: URL) throws -> [String: Any] {
        func encode(_ settings: GameSettings) throws -> Any {
            var fields = try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings)) as! [String: Any]
            fields["dismissedWarnings"] = settings.dismissedWarnings.sorted()
            return fields
        }
        let inputs = [
            ("old", #"{"fps":125,"vsync":true}"#),
            ("missing", "{}"),
            ("partial", #"{"advanced":"rate 20000","resolution":"1024x768","engine":"original","renderer":"openGL","renderScale":75,"appearance":"dark","darkMode":false,"dpi":1600,"sendCrashReports":true,"crashReportsAsked":true,"introFilmsPlayed":false,"dismissedWarnings":["one","two","one"]}"#),
            ("future", #"{"engine":"unrecognized","renderer":"vulkan","renderScale":420,"appearance":"blue","hideWhilePlaying":"false","refreshServersOnOpen":8,"dismissedWarnings":["one",4],"sendCoD2xReports":{},"checkUpdatesAutomatically":null,"introFilmsPlayed":"yes","vsync":null}"#),
            ("wrong-core-type", #"{"sensitivity":"five"}"#),
            ("integer-valued-number", #"{"fps":250.0,"anisotropy":8e0}"#),
            ("invalid-shape", #"[{}]"#),
            ("null-root", "null")
        ]
        let settings: [[String: Any]] = try inputs.map { id, input in
            var row: [String: Any] = ["id": id, "json": input]
            if let result = try? JSONDecoder().decode(GameSettings.self, from: Data(input.utf8)) {
                row["decoded"] = try encode(result)
                row["reset"] = try encode(result.reset(to: GameSettings()))
                row["centimetres"] = result.cm360
            }
            return row
        }
        let comparisons: [[String: Any]] = [("0.1.10", "0.1.9"), ("0.1.01", "0.1.1"), ("0.5", "0.5.0"),
            ("0.5.0-beta.11", "0.5.0-beta.2"), ("0.5.0-beta", "0.5.0"), ("0.5.0", "0.5.0"), ("1.4.6.8", "1.3")].map {
                ["candidate": $0, "current": $1, "newer": ReleaseNotes.isNewer($0, than: $1)]
            }
        let tags = [" Vv0.5.0 \t", "  v0.5.0-beta.12  ", "v", "VVV123456789012345678901234567890123456"].map {
            ["input": $0, "version": ReleaseNotes.version($0)]
        }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let release = try Data(contentsOf: root.appendingPathComponent("tests/launcher/fixtures/releases.json"))
        let details = Data(#"""
        [
          {"draft":true,"tag_name":"v0.6.0"},
          {"prerelease":true,"tag_name":" Vv0.5.0-rc.2 ","name":" Preview ","published_at":"2026-11-21T18:00:00Z","html_url":"https://github.com/example/release"},
          {"tag_name":"v0.4.0","body":"### Added\n- **Menu.** See [details](https://example.invalid).\n  A continuation.\n\n### Changed\n* Other thing: a note.\n\n### Removed\n- Omitted.",
            "assets":[{"name":"CoD2-Unbound-0.4.0-macos-arm64.zip","size":210,"browser_download_url":"https://github.com/cod2unbound/cod2-unbound/releases/download/v0.4.0/CoD2-Unbound-0.4.0-macos-arm64.zip"},
                      {"name":"SHA256SUMS","size":100,"browser_download_url":"https://github.com/cod2unbound/cod2-unbound/releases/download/v0.4.0/SHA256SUMS"},
                      {"name":"missing-size","browser_download_url":"https://example.invalid"}]}
        ]
        """#.utf8)
        let releases: [[String: Any]] = try [release, details, Data("[]".utf8)].enumerated().map { id, bytes in
            let feed = try ReleaseNotes.parse(bytes, calendar: calendar)
            return ["id": id, "json": String(decoding: bytes, as: UTF8.self), "latestVersion": feed.latestVersion as Any? ?? NSNull(),
                    "dispatches": feed.dispatches.map { ["stamp": $0.stamp, "title": $0.title, "summary": $0.summary,
                        "link": $0.link as Any? ?? NSNull(), "version": $0.version as Any? ?? NSNull(),
                        "date": $0.date.map { Int($0.timeIntervalSince1970) as Any } ?? NSNull()] },
                    "assets": feed.latestAssets.map { ["name": $0.name, "size": $0.size, "url": $0.url.absoluteString] },
                    "notes": feed.latestNotes.map { ["tag": $0.tag, "title": $0.title, "detail": $0.detail] }]
        }
        let assets = try ReleaseNotes.parse(details).latestAssets
        let offer: [[String: Any]] = [
            ("normal", "0.4.0", assets), ("duplicate", "0.4.0", assets + [assets[0]]), ("missing-sums", "0.4.0", [assets[0]]),
            ("version", "0.4.0-beta", assets), ("wrong-name", "0.4.1", assets),
            ("bad-root", "0.4.0", [ReleaseAsset(name: assets[0].name, size: 210, url: URL(string: "https://github.com/other/CoD2/releases/download/asset")!), assets[1]]),
            ("oversized", "0.4.0", [ReleaseAsset(name: assets[0].name, size: (1 << 30) + 1, url: assets[0].url), assets[1]])
        ].map { id, version, values in
            ["id": id, "version": version, "archive": AppUpdate.archiveName(version),
             "assets": values.map { ["name": $0.name, "size": $0.size, "url": $0.url.absoluteString] },
             "offered": AppUpdate.offer(version: version, assets: values) != nil]
        }
        let hex = String(repeating: "ab12", count: 16)
        let name = AppUpdate.archiveName("0.4.0")
        let line = "\(hex)  \(name)"
        let other = "\(String(repeating: "0", count: 64))  other.zip"
        let binary = "\(hex.uppercased()) *\(name)"
        let hashes = [line, "\(binary)\r\n", "\(other)\r\n\(binary)\r\n",
                      "\(line)\r\n\(line)\r\n", "\(line)\n\(line)\n",
                      "\(hex)  other.zip", "not a checksum"].map {
            ["input": $0, "archive": name, "hash": (try? AppUpdate.checksum(Data($0.utf8), name: name)) as Any? ?? NSNull()]
        }
        let joins: [[String: Any]] = GameEngine.allCases.flatMap { engine in
            [(nil, false), (nil, true), (["protocol":"118"], true), (["protocol":"120"], false),
             (["shortversion":"1.4.6.8", "protocol":"118"], true)].map { fields, lookedUp in
                let server = fields.map { GameServer(address: "127.0.0.1", fields: $0, ping: 50, players: []) }
                return ["engine": engine.rawValue, "fields": fields as Any? ?? NSNull(), "lookedUp": lookedUp,
                        "result": String(describing: engine.check(server: server, lookedUp: lookedUp))]
            }
        }
        let display: [[String: Any]] = [(5120,2880), (3440,1440), (2560,1600), (640,480)].map { width, height in
            let native = DisplayResolution(width, height)
            let modes = [DisplayResolution(3000, 1024), DisplayResolution(512, 288), DisplayResolution(640, 480), DisplayResolution(1024,768)]
            let sizes = DisplayResolutions.offered(native: native, modes: modes)
            return ["native": native.value, "modes": modes.map(\.value), "offered": sizes.map(\.value),
                    "initial": DisplayResolutions.initial(native: native), "fitting": DisplayResolutions.fitting("3024x1964", in: sizes),
                    "invalid": DisplayResolutions.fitting("wide", in: sizes)]
        }
        return ["fresh": try encode(GameSettings()), "settings": settings, "versions": comparisons, "tags": tags,
                "release": releases, "offers": offer, "checksums": hashes, "joins": joins, "displays": display]
    }
}
