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
        return ["cases": cases]
    }
}
