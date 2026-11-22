import Foundation

enum SetupParity {
    static func build(scratch: URL) throws -> [String: Any] {
        let root = scratch.appendingPathComponent("parity-setup")
        defer { try? FileManager.default.removeItem(at: root) }
        var cases: [[String: Any]] = []
        for indices in [Array(0..<16), Array(1..<16), Array(0..<15), []] {
            let main = root.appendingPathComponent("count-\(indices.first ?? -1)-\(indices.count)/main")
            try FileManager.default.createDirectory(at: main, withIntermediateDirectories: true)
            for i in indices {
                try Data().write(to: main.appendingPathComponent(String(format: "iw_%02d.iwd", i)))
            }
            // The explicit-folder entry point searches only this synthetic tree.
            let accepted = LauncherFindData(main.deletingLastPathComponent().path) != nil
            cases.append(["indices": indices, "accepted": accepted])
        }
        let keys = ["", "0000-0000 0000-0000 86d3", "000000000000000086D0", "000000000000000086D3\n",
                    "000000000000000086D30", "000000000000000086G3", "é😀 1234-ABcz", "000000000000000086D3\u{0}", "ß"]
        let keyCases: [[String: Any]] = keys.map { input in
            let display = KeyFormat.display(input)
            let count = KeyFormat.groups(input).joined().count
            let valid = KeyStore.valid(input)
            return ["input": input, "normalized": KeyStore.normalize(input), "display": display,
                    "valid": valid, "left": count > 0 && count < 20 ? 20 - count : 0,
                    "state": count == 0 ? "Blank" : count < 20 ? "Typing" : valid ? "Verified" : "Mismatch"]
        }
        let preferences: [[String: Any]] = [
            (nil as String?, nil as String?, false), (nil, " /old/root \n", true),
            (nil, " /old/root \n", false), ("/valid", "/old", true), ("", "/old", true)
        ].map { found, remembered, complete in
            ["found": found as Any? ?? NSNull(), "remembered": remembered as Any? ?? NSNull(),
             "complete": complete, "expected": GameDataFolder.atStart(found: found, remembered: remembered, setupComplete: complete)]
        }
        let volume = root.appendingPathComponent("volume")
        let players = volume.appendingPathComponent("Games/Call of Duty 2/main/players")
        for relative in ["Tessa/config_mp.cfg", "taylor/backup/config_mp.cfg", "taylor/config_mp.cfg",
                         "Tessa/Raptr/config_mp-old.cfg.bak", "Tessa/ignored.cfg",
                         "taylor/other/config_mp.cfg", "Tessa/config_sp.cfg"] {
            let url = players.appendingPathComponent(relative)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data().write(to: url)
        }
        try Data("Tessa\n".utf8).write(to: players.appendingPathComponent("active.txt"))
        try FileManager.default.createSymbolicLink(at: players.appendingPathComponent("Tessa/config_mp-linked.cfg"),
                                                   withDestinationURL: players.appendingPathComponent("taylor/config_mp.cfg"))
        var bounded = WindowsProfiles()
        bounded.maximumEntries = 0
        let full = WindowsProfiles().discover(mountedVolumes: [volume], selectedFolders: [players])
        let windows: [String: Any] = [
            "profiles": full.profiles.map { ["name": $0.profileName, "path": String($0.configURL.path.dropFirst(players.path.count + 1)),
                                            "active": $0.isActive, "backup": $0.isBackup] },
            "truncated": bounded.discover(mountedVolumes: [volume]).truncated
        ]
        let languages = SteamLanguage.allCases.map { language -> [String: Any] in
            let depots = SteamGame.depots(language)
            var progress = SteamProgress(depots: depots)
            var amounts = [progress.done]
            progress.file(percent: 50); amounts.append(progress.done)
            progress.wrote(15_000_000); amounts.append(progress.done)
            progress.depot(1); amounts.append(progress.done)
            progress.file(percent: 10); amounts.append(progress.done)
            progress.depot(language.depot); amounts.append(progress.done)
            progress.file(percent: 100); amounts.append(progress.done)
            progress.wrote(Int64.max / 2); amounts.append(progress.done)
            progress.finish(); amounts.append(progress.done)
            return ["language": language.rawValue, "ids": depots.map(\.id),
                    "total": progress.total, "room": SteamGame.roomNeeded(language), "amounts": amounts]
        }
        return ["cases": cases, "keys": keyCases, "preferences": preferences, "windows": windows, "steam": languages]
    }
}
