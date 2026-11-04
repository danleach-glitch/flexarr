import Foundation

/// A plural form, as CLDR names them.
public enum PluralCategory: String, Sendable, CaseIterable {
    case zero, one, two, few, many, other
}

/// CLDR's operands of a number, computed exactly as scripts/i18n/plurals.py does, so every platform picks the same
/// form: from the number's shortest decimal form, a whole number having no fraction digits.
struct PluralOperands: Sendable {
    let n: Double, i: Double, v: Double, w: Double, f: Double, t: Double, e: Double

    init(_ x: Double) {
        let n = abs(x)
        let s = "\(n)"
        guard n.rounded(.towardZero) != n, !s.contains("e"), let dot = s.firstIndex(of: ".") else {
            self.init(n: n, i: n.rounded(.towardZero), v: 0, w: 0, f: 0, t: 0)
            return
        }
        let whole = s[..<dot], frac = s[s.index(after: dot)...]
        let stripped = frac.replacing(/0+$/, with: "")
        self.init(n: n, i: Double(whole) ?? 0, v: Double(frac.count), w: Double(stripped.count),
                  f: Double(frac) ?? 0, t: Double(stripped.isEmpty ? "0" : String(stripped)) ?? 0)
    }

    private init(n: Double, i: Double, v: Double, w: Double, f: Double, t: Double) {
        (self.n, self.i, self.v, self.w, self.f, self.t, self.e) = (n, i, v, w, f, t, 0)
    }
}

public enum Plurals {
    /// The CLDR locale a BCP 47 code uses: pt-PT, then pt, then und (everything is `other`).
    public static func locale(_ code: String) -> String {
        var c = code.replacingOccurrences(of: "_", with: "-")
        while true {
            if PluralRules.byLocale[c] != nil { return c }
            guard let cut = c.lastIndex(of: "-") else { return "und" }
            c = String(c[..<cut])
        }
    }

    public static func category(_ code: String, _ x: Double) -> PluralCategory {
        PluralRules.byLocale[locale(code)]?(PluralOperands(x)) ?? .other
    }
}
