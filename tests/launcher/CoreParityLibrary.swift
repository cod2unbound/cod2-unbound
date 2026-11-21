import Foundation

enum LibraryParity {
    static func build() throws -> [String: Any] {
        let names = ["round (1)", "...", "", "mp_BréCourt_12", "ångström", "match-7", "😀\u{301}", "x:y", "a/b"]
        let key = "0000-0000-0000-0000-86D3"
        let cases = ["Reason: signal 11\n/home/test/CoD2/log\n/Users/synthetic/path\nCommand line: demo stub\n",
                     "Message: use +EXEC test.cfg\nGood line\n\n", "cdkey data: \(key)", "name=CODKEY\n",
                     "not a valid key: 0000-0000-0000-0000-86D4", "reason: \(String(repeating: "y", count: 201))"]
        let home = "/home/test"
        let crashes: [[String: Any]] = cases.map { text in
            let name = "cod2_crash_1.txt"
            let redacted = try? CrashReporting.redact(text, home: home)
            return ["text": text, "home": home, "name": name, "containsKey": CrashReporting.containsKey(text),
                    "redacted": redacted as Any? ?? NSNull(),
                    "reason": redacted.map { CrashReporting.reason($0, name: name) } as Any? ?? NSNull(),
                    "filtered": GameSessionLog.filtered(text.components(separatedBy: "\n")[0], secrets: ["synthetic-secret"])]
        }
        let now = Date(timeIntervalSince1970: 1_795_262_400)
        let dsn = SentryDSN("https://exampleKey:exampleSecret@fixture.invalid/reporting/path/123")!
        let envelope = CrashReporting.envelope(log: "cod2_crash_1.txt", text: "Reason: synthetic log\n",
            occurred: now, facts: .init(release: "0.5.0", macOS: "26.0", model: "Fixture"),
            dsn: dsn, eventID: "00000000000000000000000000000001", sentAt: now)
        let mapCases = names + ["mp_", "../toujane", "mp_toujane", "mp_custom_map", String(repeating: "x", count: 65)]
        let maps: [[String: Any]] = mapCases.map { map in
            ["map": map, "key": MapCatalog.key(map) as Any? ?? NSNull(), "display": MapCatalog.displayName(map),
             "region": MapCatalog.region(map) as Any? ?? NSNull(), "scenery": MapCatalog.scenery(map).rawValue,
             "mode": GameModes.long(map)]
        }
        let sizes = [-1, 0, 999, 1000, 1500, 999_999, 1_050_000, 1_234_567_890]
        let media: [[String: Any]] = zip(names, sizes).map { name, bytes in
            ["name": name, "bytes": bytes, "size": MediaFacts.sizeLabel(bytes),
             "map": MediaFacts.mapHint(name) as Any? ?? NSNull()]
        }
        let news = Data("""
        {"items":[
          {"date":"2026-11-20","title":"  Hello \\n world","summary":"New\\ttext","open":"configs"},
          {"date":"2026-11-21","title":"Tomorrow"},
          {"date":"2026-02-29","title":"Invalid date"},
          {"date":"2026-11-19","title":"Unsafe link","link":"https://github.com.evil.invalid/go","open":"unknown"},
          {"date":"2026-11-19","title":"Good link","link":"https://github.com/cod2unbound/cod2-unbound"},
          {"date":"2026-11-19","title":"\\u202e"}
        ],"next":{"title":"Next","summary":"Work in progress","link":"https://github.com/cod2unbound/cod2-unbound/releases"}}
        """.utf8)
        var utc = Calendar(identifier: .gregorian); utc.timeZone = .gmt
        let feed = try NewsFeed.parse(news, now: now, calendar: utc)
        func dispatch(_ item: Dispatch) -> [String: Any] {
            ["stamp": item.stamp, "title": item.title, "summary": item.summary,
             "link": item.link as Any? ?? NSNull(), "open": item.open as Any? ?? NSNull()]
        }
        return ["demos": names.map { ["name": $0, "playable": DemoName.playable($0), "alias": DemoName.playableName($0)] },
                "crashes": crashes, "envelope": envelope.base64EncodedString(), "endpoint": dsn.envelopeURL.absoluteString,
                "authorization": dsn.authorization(client: "fixture/1"), "maps": maps, "media": media,
                "news": ["input": news.base64EncodedString(), "now": now.timeIntervalSince1970,
                         "items": feed.items.map(dispatch), "next": feed.next.map(dispatch) as Any? ?? NSNull()]]
    }
}
