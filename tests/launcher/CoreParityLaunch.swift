import Foundation

enum LaunchParity {
    static func build() throws -> [String: Any] {
        let scratch = URL(fileURLWithPath: ProcessInfo.processInfo.environment["HOME"]!)
        let folder = scratch.appendingPathComponent("launch")
        let main = folder.appendingPathComponent("main")
        let chain = try ConfigStudioLaunch.chainBasename(profileID: "Daily.Stage")
        let chainFile = main.appendingPathComponent(chain)
        try FileManager.default.createDirectory(at: main, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        try Data("vstr x\n".utf8).write(to: chainFile)

        let link = try LaunchLink.direct("LOCALhost", password: "fake+password")
        let studio = ConfigStudioLaunch(profileID: "Daily.Stage", chain: chain, connectionIsolated: true)
        let noChain = ConfigStudioLaunch(profileID: "Daily.Stage", chain: nil)
        let connection = ConfigConnectionLaunch(address: "localhost:28960", values: ["SNAPS": "40", "rate": "25000"])
        var cases: [[String: Any]] = []
        func plan(_ id: String, engine: GameEngine = .cod2x, settings: GameSettings = GameSettings(),
                  dataPath: String = "/Game data", home: String = "/Player home", demo: String? = nil,
                  forwarded: [String] = [], studio: ConfigStudioLaunch? = nil, chainExists: Bool = false,
                  loadout: [String: String]? = nil, link: LaunchLink? = nil, connection: ConfigConnectionLaunch? = nil) {
            var row: [String: Any] = ["id": id, "engine": engine.rawValue, "dataPath": dataPath, "homePath": home,
                                      "forwarded": forwarded, "chainExists": chainExists,
                                      "introPlayed": settings.introFilmsPlayed, "advanced": settings.advanced,
                                      "fullscreen": settings.fullscreen, "resolution": settings.resolution,
                                      "sensitivity": settings.sensitivity, "matchSummariesEnabled": settings.matchSummariesEnabled]
            row["demo"] = demo
            row["link"] = link?.url
            row["loadout"] = loadout
            if let studio {
                var value: [String: Any] = ["profileId": studio.profileID, "connectionIsolated": studio.connectionIsolated]
                value["chain"] = studio.chain
                row["studio"] = value
            }
            if let connection { row["connection"] = ["address": connection.address, "values": connection.values] }
            do {
                let recorded = try GameLaunchPlan(settings: settings, dataPath: dataPath,
                    homePath: chainExists ? folder.path : home, link: link, demo: demo, forwarded: forwarded,
                    configStudio: studio, connection: connection, loadout: loadout.map { entries in { entries[$0.lowercased()] } },
                    engine: engine)
                row["arguments"] = recorded.arguments.map { $0 == "\"\(folder.path)\"" ? "\"\(home)\"" : $0 }
                row["playsIntroFilms"] = recorded.playsIntroFilms
                row["handoff"] = recorded.link?.url
            } catch { row["error"] = error.localizedDescription }
            cases.append(row)
        }

        for engine in GameEngine.allCases {
            plan("menu-\(engine.rawValue)", engine: engine)
            plan("demo-\(engine.rawValue)", engine: engine, demo: "round.one-2")
            plan("join-\(engine.rawValue)", engine: engine, studio: studio, chainExists: true, link: link, connection: connection)
            plan("first-loadout-\(engine.rawValue)", engine: engine, studio: noChain, loadout: [:])
        }
        var changed = GameSettings()
        changed.advanced = "SENSITIVITY 7\n+set fs_game = mods/test\nseta net_port 29971\nr_gamma=1.2"
        changed.fullscreen = "windowed"; changed.resolution = "1024x768"; changed.sensitivity = 2.0
        changed.introFilmsPlayed = true; changed.matchSummariesEnabled = false
        plan("advanced-legacy", settings: changed)
        plan("advanced-migrated", settings: changed, studio: studio, chainExists: true,
             loadout: ["r_mode": "1024x768", "r_swapinterval": "1", "in_rawmouse": "0"])
        plan("forwarded", forwarded: ["+set", "developer", "\"1\""], studio: noChain)
        plan("missing-chain", studio: studio)
        plan("unsafe-path", dataPath: "/bad/path+quit")
        plan("invalid-demo", demo: "bad demo")
        plan("invalid-profile", studio: ConfigStudioLaunch(profileID: "+quit", chain: nil))
        plan("invalid-chain", studio: ConfigStudioLaunch(profileID: "Daily.Stage", chain: "unknown.cfg"))
        plan("bad-connection-owner", link: link, connection: connection)
        plan("no-isolation", studio: noChain, link: link, connection: connection)
        plan("connection-address-mismatch", studio: studio, link: link,
             connection: ConfigConnectionLaunch(address: "other.test", values: [:]))
        plan("connection-invalid-value", studio: studio, link: link,
             connection: ConfigConnectionLaunch(address: "localhost", values: ["rate": "1+2"]))
        var bad = GameSettings()
        bad.advanced = "sv_password nope"
        plan("credentials-rejected", settings: bad)
        bad.advanced = "g_test line;quit"
        plan("unsafe-value", settings: bad)
        bad.advanced = ""; bad.resolution = "01920x1080"
        plan("invalid-resolution", settings: bad)

        var limits: [[String: Any]] = []
        for engine in GameEngine.allCases {
            let bounds = EngineCommandLine.limits(for: engine)
            let probes: [(String, [String])] = [("exact-commands", Array(repeating: "+map", count: bounds.commands)),
                ("too-many", Array(repeating: "+map", count: bounds.commands + 1)),
                ("many-too-many", Array(repeating: "+map", count: bounds.commands + 3)),
                ("exact-bytes", ["+" + String(repeating: "x", count: bounds.bytes - 1)]),
                ("too-long", ["+" + String(repeating: "é", count: bounds.bytes / 2 + 1)])]
            for (id, arguments) in probes {
                let problem = EngineCommandLine.problem(arguments, engine: engine)
                let editor = EngineCommandLine.problem(arguments, engine: engine, inEditor: true)
                limits.append(["id": "\(engine.rawValue)-\(id)", "engine": engine.rawValue, "maxCommands": bounds.commands,
                    "maxBytes": bounds.bytes, "arguments": arguments, "problem": problem as Any? ?? NSNull(), "editor": editor as Any? ?? NSNull()])
            }
        }
        var server = HostedServer.new(id: "fixture")
        server.port = 29970
        let dedicated: [[String: Any]] = try HostedServer.Reach.allCases.map { reach in
            server.reach = reach
            return ["id": reach.rawValue, "port": server.port,
                    "arguments": try HostingConfig.arguments(server, dataPath: "/Game data", home: "/Player home", bind: "127.0.0.1")]
        }
        var nativeLink = Cod2xURL()
        var connect = [CChar](repeating: 0, count: 512)
        guard Cod2x_ParseURL(link.url, &nativeLink) != 0, Cod2x_URLCommands(&nativeLink, &connect, 512) != 0 else {
            throw CocoaError(.coderInvalidValue)
        }
        return ["client": cases, "limits": limits, "dedicated": dedicated,
                "chain": chain, "connectionConfig": String(decoding: connect.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)]
    }
}
