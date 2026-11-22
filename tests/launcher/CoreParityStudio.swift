import Foundation

enum StudioParity {
    static func build(root: URL) throws -> [String: Any] {
        let resources = root.appendingPathComponent("launcher/Resources")
        let catalog = try ConfigCatalog(data: Data(contentsOf: resources.appendingPathComponent("ConfigCatalog.json")))
        let config = Data("bind F1 \"+attack\"\nbind A \"+attack\"\nbind w \"+forward\"\nbind MOUSE1 \"+attack\"\nbind q \"+forward; wait\"\nbind b \" +attack\"\nbind F3 \"say hello\"\n".utf8)
        let modes: [(ConfigDialect, ConfigCatalogMode)] = [(.stock, .stock), (.cod2x, .cod2x)]
        let bindingCases = modes.map { dialect, mode -> [String: Any] in
            let bindings = ConfigBindings(document: ConfigDocument(data: config, dialect: dialect), catalog: catalog, mode: mode)
            return ["mode": mode.rawValue, "input": config.base64EncodedString(),
                    "entries": bindings.entries.map { ["key": $0.key.spelling, "binding": $0.text,
                        "action": $0.actionName as Any? ?? NSNull(), "hasReleaseTail": $0.hasReleaseTail] },
                    "actions": bindings.actions.map { ["name": $0.name, "title": $0.title, "group": $0.group.rawValue] }]
        }
        let keyboard = [0, 8, 9, 13, 27, 32, 65, 96, 126, 127, 300, (1 << 30) | 104, (1 << 30) | 105,
                        (1 << 30) | 106, (1 << 30) | 58, (1 << 30) | 82, (1 << 30) | 227, (1 << 30) | 144]
        let inputs = keyboard.map { code -> [String: Any] in
            ["sdl": code, "number": EngineInputMapping.keyNumber(sdlKeycode: Int32(code)) as Any? ?? NSNull()]
        }
        let rules = try ConfigRules(data: Data(contentsOf: resources.appendingPathComponent("ConfigRulesets.json")))
        var seen: Set<String> = []
        let rulesCases = rules.rulesets.flatMap { policy in
            rules.resolvedRules(id: policy.id).filter { rule in
                seen.insert("\(policy.mode.rawValue)|\(rule.constraint.kind.rawValue)|\(rule.advisory)|\(rule.serverOwned)").inserted
                    || rule.id == policy.rules.first?.id
            }.flatMap { rule in
                ([nil, "", "0", "1", " 1.0 ", "1.25e2", "-9999"] as [String?]).map { value -> [String: Any] in
                    let item = rules.evaluate(setting: rule.setting, value: value, rulesetIDs: [policy.id], mode: policy.mode)
                    return ["ruleset": policy.id, "mode": policy.mode.rawValue, "value": value as Any? ?? NSNull(),
                            "setting": item.setting, "status": item.status.rawValue, "reason": item.reason,
                            "owned": item.serverOwned, "sources": item.sources.map(\.sourceURL)]
                }
            }
        }
        return ["bindings": bindingCases, "keyboard": inputs, "rules": rulesCases]
    }
}
