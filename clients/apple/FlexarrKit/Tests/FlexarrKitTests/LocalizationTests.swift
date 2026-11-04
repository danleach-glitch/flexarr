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

@Suite(.serialized) struct LocalizationTests {
    @Test func fallsBackToEnglishStringByString() {
        let l = Localization.shared
        l.show(LanguagePack(code: "de", name: "Deutsch", direction: "ltr", version: 1, messages: [
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
