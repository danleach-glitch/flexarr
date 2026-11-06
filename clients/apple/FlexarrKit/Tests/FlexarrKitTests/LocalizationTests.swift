import Foundation
import Testing
@testable import FlexarrKit

/// The shared cases every platform's formatter runs (languages/tests/format-cases.json).
@Suite struct MessageFormatTests {
    struct Cases: Decodable {
        struct Case: Decodable {
            let locale: String
            let message: String
            let args: [String: Value]
            let expect: String
        }
        enum Value: Decodable {
            case number(Double), text(String)
            init(from decoder: Decoder) throws {
                let c = try decoder.singleValueContainer()
                if let d = try? c.decode(Double.self) { self = .number(d) } else { self = .text(try c.decode(String.self)) }
            }
            var any: Any { switch self { case .number(let d): d; case .text(let s): s } }
        }
        let format: [Case]
        let errors: [String]
    }

    static let cases: Cases = {
        let url = URL(filePath: #filePath).deletingLastPathComponent().appending(path: "../../../../../languages/tests/format-cases.json").standardized
        return try! JSONDecoder().decode(Cases.self, from: Data(contentsOf: url))
    }()

    @Test func formatsEverySharedCase() throws {
        for c in Self.cases.format {
            let got = MessageFormat.format(try MessageFormat.parse(c.message), language: c.locale, values: c.args.mapValues(\.any))
            #expect(got == c.expect, "\(c.message) in \(c.locale)")
        }
    }

    @Test func refusesEverySharedError() {
        for m in Self.cases.errors {
            #expect(throws: MessageFormat.ParseError.self, "\(m)") { try MessageFormat.parse(m) }
        }
    }
}

/// These switch the language every test sees for a moment, so they use en-XB: English numbers and plurals, its own strings.
@Suite(.serialized) struct LocalizationTests {
    @Test func fallsBackToEnglishStringByString() {
        let l = Localization.shared
        l.show(LanguagePack(code: "en-XB", name: "Test", direction: "ltr", version: 1, messages: [
            "Play": "Abspielen", "music::Play": "Wiedergeben", "Broken {x}": "Kaputt {",
            "{n, plural, one {# season} other {# seasons}}": "{n, plural, one {# Staffel} other {# Staffeln}}",
        ]))
        defer { l.show(nil) }
        #expect(t("Play") == "Abspielen")
        #expect(t("Play", ctx: "music") == "Wiedergeben")
        #expect(t("{n, plural, one {# season} other {# seasons}}", ["n": 2]) == "2 Staffeln")
        #expect(t("Broken {x}", ["x": 1]) == "Broken 1")
        #expect(t("Not translated") == "Not translated")
    }

    @Test func picksTheDevicesBestOfferedLanguage() {
        #expect(Localization.deviceChoice(offered: ["de", "pt"], preferred: ["pt-BR", "de"]) == "pt")
        #expect(Localization.deviceChoice(offered: ["pt-BR"], preferred: ["pt_br"]) == "pt-BR")
        #expect(Localization.deviceChoice(offered: ["de"], preferred: ["en-GB", "de"]) == nil)
    }

    @Test func picksCldrPluralForms() {
        #expect(Plurals.category("pl", 3) == .few)
        #expect(Plurals.category("ar", 0) == .zero)
        #expect(Plurals.category("zz", 1) == .other)
    }
}

@Suite(.serialized) struct LanguageServerTests {
    @Test func aServerBeforeLanguagesOffersNone() async throws {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        #expect(try await client.languages().isEmpty)
    }

    @Test func settlesOnEnglishWhenTheServerHasNone() async {
        let (client, _) = stubbedClient(.init(status: 404, body: Data()))
        Localization.shared.show(LanguagePack(code: "en-XB", name: "Test", direction: "ltr", version: 1, messages: [:]))
        await Localization.shared.settle(profileChoice: nil, client: client)
        #expect(Localization.shared.code == "en")
    }

    @Test func readsAPackForTheAppleApps() async throws {
        let pack = #"{"code":"de","name":"Deutsch","direction":"ltr","version":5,"messages":{"Play":"Abspielen"}}"#
        let (client, host) = stubbedClient(.init(status: 200, body: Data(pack.utf8)))
        #expect(try await client.languagePack("de").messages["Play"] == "Abspielen")
        #expect(StubProtocol.recorded(host: host).last?.url?.query == "scope=apple")
    }

    @Test func sendsTheLanguageShownAsAcceptLanguage() async throws {
        let (client, host) = stubbedClient(.init(status: 404, body: Data()))
        Localization.shared.show(LanguagePack(code: "en-XB", name: "Test", direction: "ltr", version: 1, messages: [:]))
        defer { Localization.shared.show(nil) }
        _ = try? await client.languages()
        #expect(StubProtocol.recorded(host: host).last?.value(forHTTPHeaderField: "Accept-Language") == "en-XB")
    }
}

@Suite(.serialized) struct SettleTests {
    @Test func downloadsTheMostSpecificPackNotEnglish() async {
        // A server listing English first and en-XA: the pack asked for is en-XA's.
        let list = #"{"languages":[{"code":"en","name":"English","direction":"ltr","version":0},{"code":"en-XA","name":"Pseudo","direction":"ltr","version":3}]}"#
        let (client, host) = stubbedClient(.init(status: 200, body: Data(list.utf8)))
        await Localization.shared.settle(profileChoice: "en-XA", client: client)
        let asked = StubProtocol.recorded(host: host).compactMap { $0.url?.path }
        #expect(asked.contains("/api/v1/languages/en-XA"))
        #expect(!asked.contains("/api/v1/languages/en"))
        Localization.shared.show(nil)
    }
}

/// A pack is kept in a file named after the code it gives itself, and the server writes that code.
struct KeptPackTests {
    private func pack(_ code: String) -> LanguagePack {
        LanguagePack(code: code, name: "Test", direction: "ltr", version: 9, messages: ["Play": "Abspielen"])
    }

    @Test func aPacksFileIsNamedOnlyAfterALanguagesCode() {
        let folder = URL(filePath: "/tmp/flexarr-test/Languages", directoryHint: .isDirectory)
        for code in ["de", "pt-BR", "zh-Hans", "es-419", "en-XA"] {
            #expect(Localization.file(for: code, in: folder)?.path == "/tmp/flexarr-test/Languages/\(code).json", "\(code)")
        }
        for code in ["../Widgets/podcasts", "..", "de/../../x", "/etc/hosts", "de.json", "de AT", "dé", "", String(repeating: "a", count: 36)] {
            #expect(Localization.file(for: code, in: folder) == nil, "\(code)")
            #expect(!Localization.isCode(code), "\(code)")
        }
    }

    @Test func aPackThatSaysItsSomethingElseIsRefused() {
        // What was asked for, or the wider language the server fell back to.
        #expect(Localization.accepts(pack("pt-BR"), askedFor: "pt-BR"))
        #expect(Localization.accepts(pack("pt"), askedFor: "pt-BR"))
        // Another language, a narrower one, or a path: not kept, so not written anywhere.
        for code in ["de", "pt-BR-x", "../Widgets/podcasts", "pt/../../Widgets/podcasts", ""] {
            #expect(!Localization.accepts(pack(code), askedFor: "pt"), "\(code)")
            #expect(!Localization.keep(pack(code), askedFor: "pt"), "\(code)")
        }
        // Asked for by a name that's a path itself (a server's list can say anything): nothing is.
        #expect(!Localization.keep(pack("../Widgets/podcasts"), askedFor: "../Widgets/podcasts"))
    }
}
