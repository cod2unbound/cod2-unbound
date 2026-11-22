import Foundation

@main struct CoreParity {
    static func main() async throws {
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
        var settings = try SettingsParity.build(root: root)
        settings["swiftRevision"] = revision
        try fixture("settings", payload: settings, mode: mode, output: output)
        let scratch = URL(fileURLWithPath: args[5])
        let directoryScratch = scratch.appendingPathComponent("parity-directory")
        defer { try? FileManager.default.removeItem(at: directoryScratch) }
        let signatures = mode == "--check" ? try DirectoryParity.recordedSignatures(in: output) : nil
        var directory = try await DirectoryParity.build(scratch: scratch, recorded: signatures)
        directory["swiftRevision"] = revision
        try fixture("directory", payload: directory, mode: mode, output: output)
        var imports = try await ImportParity.build(catalogURL: root.appendingPathComponent("launcher/Resources/ConfigCatalog.json"))
        imports["swiftRevision"] = revision
        try fixture("import", payload: imports, mode: mode, output: output)
        let sessionScratch = scratch.appendingPathComponent("parity-import-session")
        defer { try? FileManager.default.removeItem(at: sessionScratch) }
        try await MainActor.run {
            try ConfigImportSessionTests.run(root: sessionScratch, resources: root.appendingPathComponent("launcher/Resources"))
        }
        var session = try ImportSessionParity.build()
        session["swiftRevision"] = revision
        try fixture("import-session", payload: session, mode: mode, output: output)
        var profiles = try ProfilesParity.build(scratch: scratch)
        profiles["swiftRevision"] = revision
        try fixture("profiles", payload: profiles, mode: mode, output: output)
        var share = try await ShareParity.build(catalogURL: root.appendingPathComponent("launcher/Resources/ConfigCatalog.json"), scratch: scratch)
        share["swiftRevision"] = revision
        try fixture("share", payload: share, mode: mode, output: output)
        var hosting = try HostingParity.build()
        hosting["swiftRevision"] = revision
        try fixture("hosting", payload: hosting, mode: mode, output: output)
        var setup = try SetupParity.build(scratch: scratch)
        setup["swiftRevision"] = revision
        try fixture("setup", payload: setup, mode: mode, output: output)
        var lifecycle = try LifecycleParity.build(root: root, scratch: scratch)
        lifecycle["swiftRevision"] = revision
        try fixture("lifecycle", payload: lifecycle, mode: mode, output: output)
        try await fixture("checker", payload: ["swiftRevision": revision, "cases": CheckerParity.build(root: root)], mode: mode, output: output)
        try fixture("ready", payload: ["swiftRevision": revision, "cases": ReadyParity.build(root: root)], mode: mode, output: output)
        var library = try LibraryParity.build()
        library["swiftRevision"] = revision
        try fixture("library", payload: library, mode: mode, output: output)
        var chains = try ChainsParity.build(root: root, scratch: scratch)
        chains["swiftRevision"] = revision
        try fixture("chains", payload: chains, mode: mode, output: output)
        try fixture("checkup", payload: ["swiftRevision": revision, "cases": CheckupParity.build(root: root, scratch: scratch)], mode: mode, output: output)
        try await MainActor.run {
            var extras = try ExtrasParity.build(root: root, scratch: scratch)
            extras["swiftRevision"] = revision
            try fixture("extras", payload: extras, mode: mode, output: output)
        }
        var studio = try StudioParity.build(root: root)
        studio["swiftRevision"] = revision
        try fixture("studio", payload: studio, mode: mode, output: output)
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
