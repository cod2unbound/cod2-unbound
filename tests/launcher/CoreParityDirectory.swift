import Foundation
import CryptoKit

enum DirectoryParity {
    static func recordedSignatures(in output: URL) throws -> [String: Data] {
        let data = try Data(contentsOf: output.appendingPathComponent("directory.json"))
        let payload = try JSONDecoder().decode(RecordedDirectory.self, from: data)
        return Dictionary(uniqueKeysWithValues: payload.cases.map { ($0.id, $0.signature) })
    }

    private struct RecordedDirectory: Decodable {
        struct Row: Decodable { let id: String; let signature: Data }
        let cases: [Row]
    }

    static func build(scratch: URL, recorded: [String: Data]?) async throws -> [String: Any] {
        let root = URL(string: "https://fixtures.invalid/setups/")!
        let index = root.appendingPathComponent("index.json")
        let signature = root.appendingPathComponent("index.sig")
        let config = root.appendingPathComponent("entries/sample/config.cfg")
        let profile = Data("seta sensitivity 2.1\nseta rconpassword sample-secret\nexec extra.cfg\n".utf8)
        // Published synthetic seed. It has no authority over the production directory.
        let key = try Curve25519.Signing.PrivateKey(rawRepresentation: Data(0..<32))
        let wrongKey = try Curve25519.Signing.PrivateKey(rawRepresentation: Data(32..<64))
        let trust = try ConfigDirectoryConfiguration(indexURL: index, signatureURL: signature, publicKey: key.publicKey.rawRepresentation)
        let entry: [String: Any] = [
            "id": "sample", "player": "Wren", "era": "2010", "feel": "Low sensitivity", "story": "Sample setup.",
            "styleTags": ["Classic"], "credit": "Wren", "team": "Northwind", "featured": true,
            "sourceURL": root.appendingPathComponent("credits/sample").absoluteString, "configURL": config.absoluteString,
            "provenance": ["publishedBy": "CoD2 Unbound", "retrievedAt": "2024-02-29",
                           "attribution": "self-published", "changes": "Private lines removed."],
            "facts": ["sensitivity": "2.1", "resolution": "1024x768"],
            "sha256": ConfigDirectory.digest(profile), "byteCount": profile.count, "platform": "native"
        ]
        func publication(_ entries: [[String: Any]], revision: Int = 1, version: Int = 1) -> [String: Any] {
            ["schemaVersion": version, "revision": revision, "entries": entries]
        }
        func changed(_ name: String, _ value: Any) -> [String: Any] {
            var copy = entry
            copy[name] = value
            return copy
        }
        var wrongProvenance = entry["provenance"] as! [String: String]
        wrongProvenance["unrecognized"] = "extra"
        var unknownAttribution = entry["provenance"] as! [String: String]
        unknownAttribution["attribution"] = "official"
        var invalidDate = entry["provenance"] as! [String: String]
        invalidDate["retrievedAt"] = "2023-02-29"
        var unknownRoot = publication([entry])
        unknownRoot["unrecognized"] = true
        var missingField = entry
        missingField.removeValue(forKey: "credit")
        let objects: [(String, [String: Any])] = [
            ("signed", publication([entry])), ("newer", publication([], revision: 2)), ("same-revision-edited", publication([changed("player", "Other")])),
            ("schema", publication([entry], version: 2)), ("zero-revision", publication([], revision: 0)), ("duplicate-id", publication([entry, entry])),
            ("unknown-root", unknownRoot), ("unknown-entry", publication([changed("unrecognized", true)])),
            ("missing-field", publication([missingField])), ("unknown-provenance", publication([changed("provenance", wrongProvenance)])),
            ("unknown-attribution", publication([changed("provenance", unknownAttribution)])),
            ("invalid-date", publication([changed("provenance", invalidDate)])),
            ("unknown-fact", publication([changed("facts", ["unrecognized": "extra"])])),
            ("null-facts", publication([changed("facts", NSNull())])),
            ("unknown-platform", publication([changed("platform", "other")])),
            ("bad-id", publication([changed("id", "Bad/id")])),
            ("duplicate-tags", publication([changed("styleTags", ["Classic", "Classic"])])),
            ("bad-hash", publication([changed("sha256", ConfigDirectory.digest(profile).uppercased())])),
            ("bad-size", publication([changed("byteCount", 65_536)])),
            ("control", publication([changed("player", "Wren\t") ])),
            ("insecure-url", publication([changed("configURL", "http://fixtures.invalid/config.cfg")])),
            ("queried-url", publication([changed("sourceURL", "https://fixtures.invalid/credit?x=1")])),
            ("traversing-url", publication([changed("configURL", "https://fixtures.invalid/setups/%2e%2e/config.cfg")]))
        ]
        func row(_ id: String, _ data: Data, _ sig: Data, _ finalURL: URL) async throws -> [String: Any] {
            let fetcher = DirectoryFixtureFetcher(responses: [
                index: .init(data: data, finalURL: finalURL, contentType: nil),
                signature: .init(data: sig, finalURL: signature, contentType: nil)
            ])
            let directory = ConfigDirectory(configuration: trust,
                cacheURL: scratch.appendingPathComponent("parity-directory/\(id).json"), fetcher: fetcher)
            let outcome: String
            do { outcome = try await directory.load().snapshot == nil ? "unavailable" : "verified" }
            catch let error as ConfigDirectoryError { outcome = error.localizedDescription }
            return ["id": id, "index": data.base64EncodedString(), "signature": sig.base64EncodedString(),
                    "finalURL": finalURL.absoluteString, "outcome": outcome]
        }
        // CryptoKit randomizes signing. In check mode, verify the recorded signature
        // over the regenerated input instead of demanding the same randomized bytes.
        func signed(_ id: String, _ data: Data, with signer: Curve25519.Signing.PrivateKey) throws -> Data {
            guard let recorded else { return try signer.signature(for: data) }
            guard let signature = recorded[id], signer.publicKey.isValidSignature(signature, for: data) else {
                throw CocoaError(.fileReadCorruptFile)
            }
            return signature
        }
        var cases: [[String: Any]] = []
        for (id, object) in objects {
            let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
            cases.append(try await row(id, data, signed(id, data, with: key), index))
        }
        let data = Data(base64Encoded: cases[0]["index"] as! String)!
        let sig = Data(base64Encoded: cases[0]["signature"] as! String)!
        cases.append(try await row("tampered", data + Data([32]), sig, index))
        cases.append(try await row("wrong-key", data, signed("wrong-key", data, with: wrongKey), index))
        cases.append(try await row("wrong-length", data, sig.dropLast(), index))
        cases.append(try await row("wrong-identity", data, sig, root.appendingPathComponent("other.json")))
        return ["indexURL": index.absoluteString, "signatureURL": signature.absoluteString, "publicKey": key.publicKey.rawRepresentation.base64EncodedString(),
                "productionPublicKey": ConfigDirectory.productionPublicKey, "profile": profile.base64EncodedString(), "cases": cases]
    }
}
