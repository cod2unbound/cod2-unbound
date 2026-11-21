import Foundation

enum ProfilesParity {
    private enum FixtureFailure: Error { case interrupted }

    static func build(scratch: URL) throws -> [String: Any] {
        let home = scratch.appendingPathComponent("parity-profiles")
        defer { try? FileManager.default.removeItem(at: home) }
        let store = try ConfigProfileStore(homeURL: home, dialect: .cod2x)
        let original = Data("// legacy\r\nseta sensitivity \"2\"\r\nseta name \"".utf8)
            + Data([0xe9, 0xff]) + Data("\"\r\n".utf8)
        let profile = try store.create(id: "Main", displayName: "Main", document: .init(data: original, dialect: .cod2x))
        try store.select(id: "Main", expectedActive: nil)
        let interrupted = try ConfigProfileStore(homeURL: home, dialect: .cod2x, writeFile: { data, url in
            if url.lastPathComponent == "studio.json" { throw FixtureFailure.interrupted }
            try PrivateFile.write(data, to: url)
        })
        var edited = profile.document
        try edited.setSetting(named: "sensitivity", value: "4")
        var metadata = profile.metadata
        metadata.displayName = "Changed"
        metadata.dpi = 1200
        metadata.locks = ["sensitivity"]
        metadata.notes = ["sensitivity": "Synthetic profile note"]
        metadata.lastImportedValues = ["sensitivity": "2"]
        metadata.chainDefinitions = Data([0, 255])
        do {
            _ = try interrupted.updateChain(id: "Main", document: edited, expected: profile.version,
                metadata: metadata, chain: Data("set helper +attack\n".utf8), summary: "Changed loadout")
            throw CocoaError(.fileWriteUnknown)
        } catch FixtureFailure.interrupted {}

        let root = home.appendingPathComponent("main/players/Main")
        let data = try Data(contentsOf: root.appendingPathComponent(".studio-transaction.json"))
        var journal = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        var entry = journal["entry"] as! [String: Any]
        let originalID = entry["id"] as! String
        let stableID = "47e026b5-c866-412e-ab49-a33d3b45a63a"
        // Keep the emitted schema while making the Swift store's clock and UUID reproducible.
        entry["id"] = stableID
        entry["date"] = 123.25
        journal["entry"] = entry
        let options: JSONSerialization.WritingOptions = [.sortedKeys]
        let observedConfig = try Data(contentsOf: root.appendingPathComponent("config_mp.cfg"))
        let recovered = try store.read(id: "Main")
        let restored = try store.restore(id: "Main", snapshotID: originalID, expected: recovered.version, revision: .before)
        return ["journal": try JSONSerialization.data(withJSONObject: journal, options: options).base64EncodedString(),
                "history": try JSONSerialization.data(withJSONObject: entry, options: options).base64EncodedString(),
                "id": stableID, "profileId": profile.profile.id,
                "beforeConfig": original.base64EncodedString(), "beforeMetadata": profile.version.metadata!.base64EncodedString(),
                "observedConfig": observedConfig.base64EncodedString(), "afterConfig": recovered.version.config.base64EncodedString(),
                "afterMetadata": recovered.version.metadata!.base64EncodedString(), "afterChain": recovered.version.chain!.base64EncodedString(),
                "restoredConfig": restored.data.base64EncodedString(), "restoredMetadata": restored.metadata!.base64EncodedString()]
    }
}
