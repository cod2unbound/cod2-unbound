import Foundation

@main struct CoreParity {
    static func main() throws {
        let args = CommandLine.arguments
        let mode = args[1], output = URL(fileURLWithPath: args[2])
        let revision = args[3]
        let root = URL(fileURLWithPath: args[4])
        var cases = try ConfigParity.build(catalogURL: root.appendingPathComponent("launcher/Resources/ConfigCatalog.json"))
        cases["swiftRevision"] = revision
        try fixture("config", payload: cases, mode: mode, output: output)
        var wire = try WireParity.build(root: root)
        wire["swiftRevision"] = revision
        try fixture("wire", payload: wire, mode: mode, output: output)
        var launch = try LaunchParity.build()
        launch["swiftRevision"] = revision
        try fixture("launch", payload: launch, mode: mode, output: output)
    }

    static func fixture(_ name: String, payload: [String: Any], mode: String, output: URL) throws {
        let file = output.appendingPathComponent(name + ".json")
        let rendered = try JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) + Data([10])
        if mode == "--write" {
            try rendered.write(to: file, options: .atomic)
        } else {
            guard try Data(contentsOf: file) == rendered else {
                throw NSError(domain: "CoreParity", code: 1, userInfo: [NSLocalizedDescriptionKey: "Swift results differ from \(file.path)."])
            }
        }
        print("Swift parity \(mode) \(name)")
    }
}
