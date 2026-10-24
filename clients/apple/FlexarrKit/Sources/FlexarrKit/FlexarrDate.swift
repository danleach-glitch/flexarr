import Foundation

/// Parses the timestamps the .NET server emits.
///
/// Observed forms: `2026-10-24T06:40:12.1234567+00:00`, `2026-10-24T06:40:12Z`, and — for
/// `DateTime` values with unspecified kind — `2024-03-18T02:13:44` or `2026-10-24T04:10:54.973171`
/// with no offset at all. The server stores UTC, so a missing offset is read as UTC.
/// Fractional seconds may have any number of digits (.NET writes up to 7).
public enum FlexarrDate {
    public static func parse(_ string: String) -> Date? {
        let s = Array(string.utf8)
        var i = 0

        func number(_ digits: Int) -> Int? {
            guard i + digits <= s.count else { return nil }
            var value = 0
            for _ in 0..<digits {
                let c = s[i]
                guard c >= 48, c <= 57 else { return nil }
                value = value * 10 + Int(c - 48)
                i += 1
            }
            return value
        }

        func expect(_ char: UInt8) -> Bool {
            guard i < s.count, s[i] == char else { return false }
            i += 1
            return true
        }

        guard let year = number(4), expect(UInt8(ascii: "-")),
              let month = number(2), expect(UInt8(ascii: "-")),
              let day = number(2) else { return nil }

        var hour = 0, minute = 0, second = 0
        var fraction = 0.0
        if i < s.count, s[i] == UInt8(ascii: "T") || s[i] == UInt8(ascii: " ") {
            i += 1
            guard let h = number(2), expect(UInt8(ascii: ":")), let m = number(2) else { return nil }
            hour = h
            minute = m
            if expect(UInt8(ascii: ":")) {
                guard let sec = number(2) else { return nil }
                second = sec
                if i < s.count, s[i] == UInt8(ascii: ".") || s[i] == UInt8(ascii: ",") {
                    i += 1
                    var scale = 0.1
                    var digits = 0
                    while i < s.count, s[i] >= 48, s[i] <= 57 {
                        fraction += Double(s[i] - 48) * scale
                        scale /= 10
                        digits += 1
                        i += 1
                    }
                    guard digits > 0 else { return nil }
                }
            }
        }

        var offsetSeconds = 0
        if i < s.count {
            switch s[i] {
            case UInt8(ascii: "Z"), UInt8(ascii: "z"):
                i += 1
            case UInt8(ascii: "+"), UInt8(ascii: "-"):
                let sign = s[i] == UInt8(ascii: "-") ? -1 : 1
                i += 1
                guard let oh = number(2) else { return nil }
                _ = expect(UInt8(ascii: ":"))
                let om = number(2) ?? 0
                offsetSeconds = sign * (oh * 3600 + om * 60)
            default:
                return nil
            }
        }
        guard i == s.count else { return nil }
        guard (1...12).contains(month), (1...31).contains(day), hour < 24, minute < 60, second < 61 else { return nil }

        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        components.hour = hour
        components.minute = minute
        components.second = second
        guard let base = utcCalendar.date(from: components) else { return nil }
        return base.addingTimeInterval(fraction - Double(offsetSeconds))
    }

    private static let utcCalendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }()
}
