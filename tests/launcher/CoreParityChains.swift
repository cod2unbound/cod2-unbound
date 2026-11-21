import Foundation

enum ChainsParity {
    static func build(root: URL, scratch: URL) throws -> [String: Any] {
        let scripts: [(String, String)] = [
            ("direct", "seta sensitivity 2; echo hello"), ("non-yielding recursion", "set a \"vstr a\"; vstr a"),
            ("yielding loop", "set a \"wait; vstr a\"; vstr a"), ("missing exec", "exec ../outside"),
            ("missing vstr", "vstr missing"), ("negative wait", "wait -12junk"),
            ("bounded vstr", "set a \"" + String(repeating: "x", count: 1023) + "\"; vstr a"),
            ("large buffer", String(repeating: "x", count: 65_536)), ("nul", "echo a\0echo b"),
            ("archive unsafe", "seta a \"a\\\"b\"; wait; set a ok"), ("case folds", "set A \"echo ready\"; vstr a"),
        ]
        func trace(_ value: ConfigChainTrace) -> [String: Any] {
            ["complete": value.complete, "commands": value.commands.map { $0.map(ConfigDocument.text) },
                "issues": value.issues.map { String(describing: $0).split(separator: "(").first.map(String.init)! }]
        }
        let traces = [ConfigDialect.stock, .cod2x].flatMap { dialect in
            scripts.map { name, text -> [String: Any] in
                var machine = ConfigChainMachine(dialect: dialect)
                return ["id": name, "cod2x": dialect == .cod2x, "input": Data(text.utf8).base64EncodedString(),
                    "trace": trace(machine.execute(Data(text.utf8)))]
            }
        }
        let home = scratch.appendingPathComponent("parity-chains")
        defer { try? FileManager.default.removeItem(at: home) }
        let store = try ConfigProfileStore(homeURL: home, dialect: .cod2x)
        let original = Data("bind h \"+attack\"\nseta sensitivity \"1.5\"\n".utf8)
        let profile = try store.create(id: "Chains", displayName: "Chains", document: .init(data: original, dialect: .cod2x))
        let chains = [
            ConfigChain(id: "HUD", key: "h", behavior: .commandCycle(steps: [["set cg_drawFPS Off"], ["set cg_drawFPS Simple"]]), name: "HUD cycle"),
            ConfigChain(id: "sens", key: "k", behavior: .settingCycle(name: "sensitivity", values: ["1.5", "2"])),
            ConfigChain(id: "regular", key: "l", behavior: .commands(["+attack", "wait", "-attack"]))
        ]
        let ready = try ConfigChains.prepare(chains: chains, read: profile)
        _ = try ConfigChains.apply(ready, to: store, replaceConflicts: true)
        let after = try store.read(id: "Chains")
        let portable = try ConfigChains.windowsExport(read: after)
        let compiled: [String: Any] = [
            "input": original.base64EncodedString(), "config": ready.document.serialized().base64EncodedString(),
            "script": ready.script.base64EncodedString(), "definitions": ready.metadata.chainDefinitions!.base64EncodedString(),
            "vars": ready.runtimeVariables, "conflicts": ready.conflictingKeys, "portable": portable.base64EncodedString(),
        ]
        return ["traces": traces, "compiled": compiled]
    }
}
