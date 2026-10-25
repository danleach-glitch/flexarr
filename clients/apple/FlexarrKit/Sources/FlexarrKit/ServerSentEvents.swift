import Foundation

/// Reads a server-sent events stream line by line: `event:` names the event, `data:` lines (joined with newlines)
/// are its body, a blank line ends it, and lines starting with `:` are comments (keep-alives).
public struct ServerSentEvents: Sendable {
    public struct Event: Sendable, Equatable {
        public let name: String
        public let data: String
    }

    private var name = "message"
    private var data: [String] = []

    public init() {}

    /// Feeds one line (without its line break); returns the event it completes, if any.
    public mutating func feed(_ line: String) -> Event? {
        if line.isEmpty {
            guard !data.isEmpty else {
                name = "message"
                return nil
            }
            let event = Event(name: name, data: data.joined(separator: "\n"))
            name = "message"
            data = []
            return event
        }
        if line.hasPrefix(":") { return nil }
        let (field, value) = Self.split(line)
        switch field {
        case "event": name = value
        case "data": data.append(value)
        default: break // id and retry aren't used here.
        }
        return nil
    }

    private static func split(_ line: String) -> (String, String) {
        guard let colon = line.firstIndex(of: ":") else { return (line, "") }
        var value = line[line.index(after: colon)...]
        if value.hasPrefix(" ") { value = value.dropFirst() }
        return (String(line[..<colon]), String(value))
    }
}
