import Foundation

enum ImportSessionParity {
    static func build() throws -> [String: Any] {
        let catalog = ConfigImportSessionTests.catalog
        let stockTarget = "seta sensitivity 2\r\nbind MOUSE1 +attack\r\n"
        let tests: [(String, String, String?, [String], ConfigImportPlatform, Bool)] = [
            ("default", "seta sensitivity 4\nseta name New\nseta password test-only\nexec absent.cfg\nunknown_forwarded\n", nil, [], .native, false),
            ("everything", "seta sensitivity 4\nseta name New\nseta password test-only\nexec absent.cfg\nunknown_forwarded\n", nil, [], .native, true),
            ("locked", "seta sensitivity 8\nbind MOUSE1 +forward\nbind w \"toggle sensitivity 1 9\"\nunbindall\nseta name Other\n", stockTarget, ["SENSITIVITY", "bind:mouse1"], .native, true),
            ("windows-source", "seta sensitivity 6\nseta r_mode 4\nseta r_fullscreen 1\nseta in_mouse 1\nbind 0x00 +attack\n", nil, [], .windows, true),
            ("nested-machine", "seta sensitivity 4\nbind w \"toggle r_mode 1 2\"\n", nil, [], .windows, true),
            ("renderer-and-key", "seta r_rendererPreference dx9\nseta r_renderer gl\nbind COMMAND +attack\n", nil, [], .windows, true),
            ("target-nul", "seta sensitivity 8\n", "seta sensitivity 2\u{0}ignored", [], .native, false),
            ("locked-helper", "seta fire \"+forward\"\nseta sensitivity 5\n", "seta fire \"+attack\"\nbind MOUSE1 \"vstr fire\"\n", ["action:+attack"], .native, true),
            ("old-key-secret", "bind w +forward\n", "bind w \"rconpassword synthetic-baseline-secret\"\n", [], .native, false),
            ("omitted-helper", "seta helper \"sensitivity 7\"\nbind w \"vstr helper\"\n", nil, [], .native, true),
        ]
        var cases: [[String: Any]] = []
        for (id, input, prior, locks, platform, everything) in tests {
            let file = ConfigImportFile(path: "config.cfg", data: Data(input.utf8), dialect: .cod2x)
            let bundle = ConfigImportBundle(source: .init(kind: .paste), entryPath: file.path, files: [file])
            var session = try ConfigImportSession(bundle: bundle, dialect: .cod2x, catalog: catalog, displayName: "Imported", platform: platform)
            if let prior {
                let bytes = Data(prior.utf8)
                var metadata = ConfigStudioMetadata(displayName: "Main")
                metadata.locks = Set(locks)
                session.setTarget(.existing(.init(profile: .init(id: "Main", displayName: "Main", isActive: true),
                    document: ConfigDocument(data: bytes, dialect: .cod2x), metadata: metadata, version: .init(config: bytes, metadata: nil))))
            }
            if everything { session.setMode(.everything) }
            if id == "omitted-helper" {
                try session.select(session.rows[0].id, selected: false)
            }
            var value: [String: Any] = [
                "id": id, "input": input, "prior": prior as Any? ?? NSNull(), "locks": locks, "platform": platform.rawValue,
                "everything": everything, "selected": session.selectedIDs.sorted(), "rows": session.rows.map {
                    ["id": $0.id, "eligible": $0.canSelect, "old": $0.oldValue as Any? ?? NSNull(),
                     "new": $0.newValue as Any? ?? NSNull(), "locks": $0.lockedNames,
                     "platformNote": $0.platformNote as Any? ?? NSNull()]
                }
            ]
            do {
                let preview = try session.preview()
                value["document"] = preview.document.serialized().base64EncodedString()
                value["lastImportedValues"] = preview.metadata.lastImportedValues
                value["preview"] = "accepted"
            } catch let error as ConfigImportSessionError {
                value["preview"] = error.localizedDescription
            }
            cases.append(value)
        }
        return ["cases": cases]
    }
}
