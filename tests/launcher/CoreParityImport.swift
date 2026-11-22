import Foundation

enum ImportParity {
    static func build(catalogURL: URL) async throws -> [String: Any] {
        var cases: [[String: Any]] = []
        func add(_ review: ConfigImportReview, catalog: String) {
            let bundle = review.bundle
            cases.append(["catalog": catalog, "dialect": bundle.files[0].document.dialect == .stock ? "stock" : "cod2x",
                          "entry": bundle.entryPath, "files": bundle.files.map { ["path": $0.path, "bytes": $0.document.serialized().base64EncodedString()] },
                          "candidates": review.candidates.map(candidate)])
        }
        ConfigImportTests.safety { add($0, catalog: "import") }
        ConfigImportSafetyTests.safety { add($0, catalog: "persistence") }

        let synthetic = Data("seta unknown_helper ".utf8) + Data([0xe9, 0xff]) + Data("\nbind j +attack\n".utf8)
        for dialect in [ConfigDialect.stock, .cod2x] {
            let file = ConfigImportFile(path: "legacy.cfg", data: synthetic, dialect: dialect)
            add(ConfigImportSafety(catalog: ConfigImportTests.catalog).review(
                .init(source: .init(kind: .paste), entryPath: file.path, files: [file])), catalog: "import")
        }

        let catalog = ConfigImportCatalogAdapter(catalog: try ConfigCatalog(data: Data(contentsOf: catalogURL)))
        let cleanInputs: [(String, Data)] = [
            ("mixed", Data("""
             // sample
             seta sensitivity "3"
             set rconpassword sample-secret
             seta name "Wren"
             seta sv_hostname "Sample server"
             bind MOUSE4 "+attack;wait;-attack"
             bind k "say hello"
             bind j "weapnext"
             exec profile.cfg
             wait
             """.utf8)),
            ("legacy", Data("seta sensitivity ".utf8) + Data([0xe9, 0xff]) + Data("\r\nseta r_vsync 1\r\n".utf8)),
            ("empty", Data())
        ]
        var cleaned: [[String: Any]] = []
        for (id, input) in cleanInputs {
            let result = try await ConfigClean.clean(input, catalog: catalog)
            cleaned.append(["id": id, "input": input.base64EncodedString(), "bytes": result.bytes.base64EncodedString(),
                            "settingLines": result.settingLines, "binds": result.binds, "kept": result.kept, "comments": result.comments,
                            "settings": result.settings, "reasons": result.reasons,
                            "leftOut": result.leftOut.map {
                                ["start": $0.range.lowerBound, "end": $0.range.upperBound, "text": $0.text.base64EncodedString(),
                                 "reasons": $0.reasons, "tokens": $0.tokens]
                            }])
        }
        return ["review": cases, "clean": cleaned]
    }

    private static func location(_ value: ConfigImportLocation) -> [String: Any] {
        ["file": value.file, "context": value.context, "start": value.range.lowerBound, "end": value.range.upperBound]
    }

    private static func candidate(_ value: ConfigImportCandidate) -> [String: Any] {
        [
            "location": location(value.location), "classification": value.classification.rawValue,
            "importable": value.importable, "default": value.selectedByDefault,
            "reasons": value.reasons.map(\.rawValue), "details": value.details,
            "effects": value.effects.map {
                ["location": location($0.location), "classification": $0.classification.rawValue,
                 "reasons": $0.reasons.map(\.rawValue), "details": $0.details]
            },
            "dependencies": value.dependencies.map {
                ["kind": $0.kind == .exec ? "exec" : "vstr", "name": $0.name, "resolved": $0.resolved,
                 "locations": $0.locations.map(location)]
            }
        ]
    }
}
