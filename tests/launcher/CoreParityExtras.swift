import Foundation

enum ExtrasParity {
    @MainActor static func build(root: URL, scratch: URL) throws -> [String: Any] {
        let input = scratch.appendingPathComponent("parity-artwork")
        let paths = try FileManager.default.contentsOfDirectory(at: input, includingPropertiesForKeys: nil)
        let all = (paths + [input.appendingPathComponent("main/iw_09.iwd")]).sorted { $0.lastPathComponent < $1.lastPathComponent }
        let artwork: [[String: Any]] = try all.filter { ["iwi", "iwd"].contains($0.pathExtension) }.map { url in
            let bytes = try Data(contentsOf: url)
            var row: [String: Any] = ["id": url.lastPathComponent, "bytes": bytes.base64EncodedString(), "archive": url.pathExtension == "iwd"]
            if url.pathExtension == "iwd" {
                let archive = try? IWDArtworkArchive(url: url)
                row["validArchive"] = archive != nil
                row["image"] = (try? archive?.image(named: "images/loadscreen_mp_toujane.iwi").base64EncodedString()) as Any? ?? NSNull()
            } else {
                let result = try IWIImageDecoder.decode(bytes)
                row["width"] = result.width; row["height"] = result.height
                row["rgba"] = result.rgba.base64EncodedString()
            }
            return row
        }
        let iwd = try Data(contentsOf: input.appendingPathComponent("stored.iwd"))
        // Build a tiny regular ZIP with just the two entries used as signatures by the extraction rule.
        func le(_ text: [UInt8], _ size: Int) -> Data { Data(text) + Data(repeating: 0, count: size - text.count) }
        var archive = Data(), central = Data()
        for name in ["ui_mp/main.menu", "materials/radar_player_arrow"] {
            let offset = archive.count
            archive += le([0x50,0x4b,3,4], 26) + Data([UInt8(name.utf8.count),0,0,0]) + Data(name.utf8)
            central += le([0x50,0x4b,1,2], 28) + Data([UInt8(name.utf8.count),0]) + Data(repeating: 0, count: 12)
            central += Data([UInt8(offset),0,0,0]) + Data(name.utf8)
        }
        let centralLength = central.count, centralStart = archive.count
        archive += central + Data([0x50,0x4b,5,6,0,0,0,0,2,0,2,0,UInt8(centralLength),0,0,0,UInt8(centralStart),0,0,0,0,0])
        var pe = Data([0x4d,0x5a]) + Data(repeating: 0, count: 58) + Data([64,0,0,0,0x50,0x45,0,0])
        pe += Data("fixture".utf8)
        let cod2x = [pe, pe + archive, pe + archive + archive, pe + iwd, Data([0]), pe + Data(archive.dropLast())].map { bytes -> [String: Any] in
            ["dll": bytes.base64EncodedString(), "archive": (try? CoD2xFiles.archive(fromDLL: bytes).base64EncodedString()) as Any? ?? NSNull()]
        }

        let resources = root.appendingPathComponent("launcher/Resources")
        let catalog = try ConfigCatalog(data: Data(contentsOf: resources.appendingPathComponent("ConfigCatalog.json")))
        let config: [[String: Any]] = ["seta nope 0\nseta r_fullscreen 1\n", "// Comment\r\nseta sensitivity \"5\n", "seta x 1\0rest",
            "seta cl_maxpackets 125\n", "seta name \"€\""].map { text in
            let result = ConfigTextCheck.issues(text: text, dialect: .cod2x, catalog: catalog, mode: .cod2x)
            return ["text": text, "bytes": result.data?.base64EncodedString() as Any? ?? NSNull(),
                "issues": result.issues.map { ["line": $0.line, "column": $0.column, "message": $0.message,
                    "excerpt": $0.excerpt, "blocking": $0.blocking, "start": $0.range.location, "length": $0.range.length] }]
        }
        let values: [[String: Any]] = ["sensitivity", "snaps", "m_filter", "snd_volume", "r_picmip", "g_password", "name"].compactMap { name in
            guard let setting = catalog.setting(named: name), let spec = ConfigSettingSpec(setting: setting, mode: .cod2x) else { return nil }
            let values = ["0", "1", "5.000", "0.75", "-1.5e2xxx", "2147483648", "fred", ""]
            return ["setting": name, "default": spec.defaultText as Any? ?? NSNull(), "choices": spec.choices,
                "values": values.map { ["raw": $0, "text": ConfigValues.display(spec, $0), "same": ConfigValues.same(spec, $0, "5"),
                    "typed": ConfigValues.number($0, type: spec.type) as Any? ?? NSNull()] }]
        }
        let ids = ["Fast loadout", "  Élément z ", "-..", "Ｓｔｏｃｋ", "two__ words", String(repeating: "a", count: 60)]
        let existing = ["fast_loadout", "Fast_loadout_2", "Stock"]
        let loadouts = ids.map { ["input": $0, "id": ParityProfileName.identifier(for: $0, existing: existing)] }

        let addr = "test.invalid:28960"
        var library = LauncherLibrary()
        library.favorites = [addr]
        library.recent = [addr, "other.invalid:28960"]
        library.cache = [GameServer(address: addr, fields: ["mapname":"mp_toujane","protocol":"120"],
            ping: 41, players: [.init(id: 0, score: -4, ping: 8, name: "Synthetic")])]
        library.cachedAt = Date(timeIntervalSinceReferenceDate: 123.5)
        library.newsNext = Dispatch(stamp: "NEXT", title: "News", summary: "", link: nil, open: "configs")
        library.played = [addr: Date(timeIntervalSinceReferenceDate: 234.5), "removed.invalid": Date(timeIntervalSinceReferenceDate: 456)]
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let libraryData = try encoder.encode(library)
        let read = try JSONDecoder().decode(LauncherLibrary.self, from: libraryData)

        let addresses = [" EXAMPLE.invalid ", "localhost:28962", " connect/localhost ", "cod2://%2Bconnect+LOCALHOST:28961/",
            "cod2x://connect%20localhost%20password%20%22synthetic%22", "localhost:99999", "foo;exec evil",
            "cod2x://connect+localhost+quit/", "hello/world", ""]
        let serverCases: [[String: Any]] = addresses.map { value in
            let result = ServerAddressInput.parse(value)
            return ["input": value, "address": result?.address as Any? ?? NSNull(), "password": result?.password as Any? ?? NSNull(),
                    "link": ServerAddressInput.joinLink(value) as Any? ?? NSNull(), "problem": ServerAddressInput.passwordProblem(value) as Any? ?? NSNull()]
        }

        var learner = PointerButtonLearning()
        var inputCases: [[String: Any]] = []
        for value in [0, 0, 1, -1, 7, 3, 9, 9, -1] {
            if value == -1 { learner.skip() } else { learner.click(value) }
            let position: String
            switch learner.step { case .position(let p): position = p.rawValue; case .extras: position = "extras"; case .done: position = "done" }
            inputCases.append(["button": value, "position": position, "note": learner.note as Any? ?? NSNull()])
        }

        let sessionHome = scratch.appendingPathComponent("parity-studio-session")
        defer { try? FileManager.default.removeItem(at: sessionHome) }
        let store = try ConfigProfileStore(homeURL: sessionHome, dialect: .cod2x)
        let session = try ConfigStudioSession(home: sessionHome, dialect: .cod2x)
        try session.initialize(settings: GameSettings())
        let active = try store.activeSelection().profileID!
        _ = try store.create(id: "Other", displayName: "Other", document: .init(data: Data("seta sensitivity 5\n".utf8), dialect: .cod2x))
        let selection = Data((active + "\r\n// retained selection\r\n").utf8)
        let activeUrl = sessionHome.appendingPathComponent("main/players/active.txt")
        try selection.write(to: activeUrl)
        let launch = try session.prepareLaunch(settings: GameSettings(), connectionProfileID: "Other")!
        let temporary = try Data(contentsOf: activeUrl)
        session.gameLaunched(pid: 12345)
        let restarted = try ConfigStudioSession(home: sessionHome, dialect: .cod2x)
        try restarted.reconcileGames([12345])
        let reserved = restarted.isReadOnly
        try restarted.reconcileGames([])
        let restored = try Data(contentsOf: activeUrl)

        let preferencesCases = [
            #"{}"#,
            #"{"filters":{"version":"CoD2x 1.4.6.8","mode":"tdm","maxPing":100,"hideEmpty":true,"hideFull":true,"hidePassworded":true},"launchedAt":{"localhost:28960":123.5}}"#,
            #"{"filters":{"version":"new-version","mode":false,"maxPing":51,"hideEmpty":1},"launchedAt":{"localhost:28960":"damaged"}}"#,
        ]
        let preferences: [[String: Any]] = try preferencesCases.map { raw in
            let prefs = try JSONDecoder().decode(ServerBrowserPrefs.self, from: Data(raw.utf8))
            return ["raw": raw, "count": prefs.filters.count, "version": prefs.filters.version.rawValue,
                "admits": prefs.filters.admits(library.cache[0]), "launched": prefs.launchedAt.mapValues(\.timeIntervalSinceReferenceDate)]
        }
        let original = Data("seta sensitivity 2.0\nseta snd_volume 0.2\nbind F1 \"+attack\"\n".utf8)
        let modified = Data("seta sensitivity 2\nseta snd_volume 0.4\nbind F1 \"+forward\"\nseta other 0\n".utf8)
        let specs = Dictionary(uniqueKeysWithValues: catalog.settings.compactMap { setting in
            ConfigSettingSpec(setting: setting, mode: .cod2x).map { (setting.name.lowercased(), $0) }
        })
        let diff = ConfigDiff.changes(from: original, nil, to: modified, nil, chains: (nil, Data("echo hello".utf8)), specs: specs, catalog: catalog, dialect: .cod2x)
        let change: [String: Any] = ["original": original.base64EncodedString(), "modified": modified.base64EncodedString(),
            "changes": diff.map { ["id": $0.id, "label": $0.label, "before": $0.then, "after": $0.now] }, "summary": ConfigDiff.sentence(diff)]
        let duration = [-1.0, 0, 0.49, 0.5, 59.5, 90.5, 3_901, 1e300].map { ["seconds": $0, "result": ParityMatchFacts.duration($0)] }
        let contrast: [[String: Any]] = [(0.0, 0.96), (0.1, 0.95), (0.9, 0.98), (1.0, 0.03)].map { low, high in
            ["low": low, "high": high, "dark": ScrimMath.darkScrim(high: high, text: 0.9),
             "light": ScrimMath.lightWash(low: low, text: 0.02, paper: 0.95)]
        }

        return ["artwork": artwork, "cod2x": cod2x, "config": config, "values": values, "loadouts": loadouts,
            "preferences": preferences, "diff": change, "duration": duration, "contrast": contrast,
            "existing": existing, "library": ["data": libraryData.base64EncodedString(), "played": read.played.keys.sorted()],
            "addresses": serverCases, "input": inputCases, "session": ["main": active, "launch": launch.profileID, "reserved": reserved,
                "originalSelection": selection.base64EncodedString(), "temporary": temporary.base64EncodedString(),
                "restored": restored.base64EncodedString()]]
    }
}
