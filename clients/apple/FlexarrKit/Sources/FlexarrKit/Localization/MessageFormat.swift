import Foundation

/// The ICU MessageFormat subset Flexarr's messages use (docs/languages.md): text, {value}, {value, number},
/// {value, plural, …} with # and =N, {value, select, …}, and ICU's apostrophe quoting. scripts/i18n/icu.py is the
/// reference; languages/tests/format-cases.json keeps the platforms in step.
public enum MessageFormat {
    public indirect enum Part: Sendable, Equatable {
        case text(String)
        case pound
        case arg(name: String, kind: Kind, options: [String: [Part]])
    }

    public enum Kind: String, Sendable { case simple, number, plural, select }

    public struct ParseError: Error, Equatable { public let message: String }

    public static func parse(_ message: String) throws -> [Part] {
        var p = Parser(chars: Array(message))
        let parts = try p.parts(inPlural: false, nested: false)
        if p.pos != p.chars.count { throw ParseError(message: "unexpected '}' at \(p.pos)") }
        return parts
    }

    public static func format(_ parts: [Part], language: String, values: [String: Any]) -> String {
        var out = ""
        write(parts, language: language, values: values, pound: nil, into: &out)
        return out
    }

    private static func write(_ parts: [Part], language: String, values: [String: Any], pound: Double?, into out: inout String) {
        for part in parts {
            switch part {
            case .text(let s): out += s
            case .pound: out += pound.map { number($0, language) } ?? "#"
            case .arg(let name, let kind, let options):
                guard let value = values[name] else {
                    out += "{\(name)}"
                    continue
                }
                switch kind {
                case .plural:
                    let n = double(value)
                    let exact = n.rounded(.towardZero) == n ? "=\(Int(n))" : nil
                    let chosen = exact.flatMap { options[$0] } ?? options[Plurals.category(language, n).rawValue] ?? options["other"] ?? []
                    write(chosen, language: language, values: values, pound: n, into: &out)
                case .select:
                    write(options["\(value)"] ?? options["other"] ?? [], language: language, values: values, pound: pound, into: &out)
                case .simple, .number:
                    if let n = numeric(value) { out += number(n, language) } else { out += "\(value)" }
                }
            }
        }
    }

    private static func numeric(_ v: Any) -> Double? {
        switch v {
        case let i as Int: Double(i)
        case let i as Int64: Double(i)
        case let d as Double: d
        case let f as Float: Double(f)
        default: nil
        }
    }

    private static func double(_ v: Any) -> Double { numeric(v) ?? Double("\(v)") ?? 0 }

    private static func number(_ n: Double, _ language: String) -> String {
        n.formatted(.number.precision(.fractionLength(0...3)).locale(Localization.formattingLocale(for: language)))
    }

    private struct Parser {
        let chars: [Character]
        var pos = 0

        mutating func parts(inPlural: Bool, nested: Bool) throws -> [Part] {
            var parts: [Part] = []
            var text = ""
            func flush() {
                if !text.isEmpty { parts.append(.text(text)); text = "" }
            }
            while pos < chars.count {
                let c = chars[pos]
                if c == "'" {
                    let next: Character? = pos + 1 < chars.count ? chars[pos + 1] : nil
                    if next == "'" { text += "'"; pos += 2; continue }
                    if next == "{" || next == "}" || (inPlural && next == "#") {
                        var end = pos + 1
                        while true {
                            guard end < chars.count else { throw ParseError(message: "unterminated quote") }
                            if chars[end] == "'" {
                                if end + 1 < chars.count, chars[end + 1] == "'" { text += "'"; end += 2; continue }
                                break
                            }
                            text.append(chars[end])
                            end += 1
                        }
                        pos = end + 1
                        continue
                    }
                    text += "'"
                    pos += 1
                } else if c == "{" {
                    flush()
                    pos += 1
                    parts.append(try arg())
                } else if c == "}" {
                    guard nested else { throw ParseError(message: "unexpected '}' at \(pos)") }
                    break
                } else if c == "#", inPlural {
                    flush()
                    parts.append(.pound)
                    pos += 1
                } else {
                    text.append(c)
                    pos += 1
                }
            }
            flush()
            return parts
        }

        mutating func skipSpace() {
            while pos < chars.count, chars[pos].isWhitespace { pos += 1 }
        }

        mutating func word() -> String {
            let start = pos
            while pos < chars.count, chars[pos].isLetter || chars[pos].isNumber || "_=-".contains(chars[pos]) { pos += 1 }
            return String(chars[start..<pos])
        }

        static func isName(_ w: String) -> Bool {
            guard let f = w.first, f.isASCII, f.isLetter || f == "_" else { return false }
            return w.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") }
        }

        static func isSelector(_ w: String) -> Bool {
            if w.hasPrefix("=") { return w.count > 1 && w.dropFirst().allSatisfy { $0.isASCII && $0.isNumber } }
            guard let f = w.first, f.isASCII, f.isLetter || f == "_" else { return false }
            return w.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-") }
        }

        mutating func arg() throws -> Part {
            skipSpace()
            let name = word()
            guard Self.isName(name) else { throw ParseError(message: "bad value name '\(name)'") }
            skipSpace()
            if pos < chars.count, chars[pos] == "}" { pos += 1; return .arg(name: name, kind: .simple, options: [:]) }
            guard pos < chars.count, chars[pos] == "," else { throw ParseError(message: "expected ',' or '}' after {\(name)") }
            pos += 1
            skipSpace()
            let kindWord = word()
            skipSpace()
            if kindWord == "number" {
                guard pos < chars.count, chars[pos] == "}" else { throw ParseError(message: "{\(name), number} takes no style") }
                pos += 1
                return .arg(name: name, kind: .number, options: [:])
            }
            guard let kind = Kind(rawValue: kindWord), kind == .plural || kind == .select else {
                throw ParseError(message: "unsupported type '\(kindWord)' in {\(name)}")
            }
            guard pos < chars.count, chars[pos] == "," else { throw ParseError(message: "expected ',' after {\(name), \(kindWord)") }
            pos += 1
            var options: [String: [Part]] = [:]
            while true {
                skipSpace()
                if pos < chars.count, chars[pos] == "}" { pos += 1; break }
                let sel = word()
                guard Self.isSelector(sel), !(kind == .select && sel.hasPrefix("=")) else { throw ParseError(message: "bad selector '\(sel)' in {\(name)}") }
                guard options[sel] == nil else { throw ParseError(message: "selector '\(sel)' twice in {\(name)}") }
                skipSpace()
                guard pos < chars.count, chars[pos] == "{" else { throw ParseError(message: "expected '{' after '\(sel)' in {\(name)}") }
                pos += 1
                let sub = try parts(inPlural: kind == .plural, nested: true)
                guard pos < chars.count, chars[pos] == "}" else { throw ParseError(message: "unclosed option '\(sel)' in {\(name)}") }
                pos += 1
                options[sel] = sub
            }
            guard options["other"] != nil else { throw ParseError(message: "{\(name), \(kindWord)} needs an 'other'") }
            return .arg(name: name, kind: kind, options: options)
        }
    }
}
