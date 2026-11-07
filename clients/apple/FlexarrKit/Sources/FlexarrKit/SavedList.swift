import Foundation

/// A list an app keeps in a file between launches (the iPhone's downloads, say), read so that an update can't lose it.
/// Each entry is read on its own, so one an older or newer app wrote differently doesn't take the rest with it. When
/// any entry can't be read, the file is first copied aside, so the next save can't overwrite what was there.
public enum SavedList {
    public struct Loaded<Element> {
        public let items: [Element]
        /// Entries that couldn't be read (and the whole file, when it wasn't a list at all).
        public let unreadable: Int
        /// Where the file was copied to before anything was dropped.
        public let keptAt: URL?
    }

    public static func load<Element: Decodable>(_ type: Element.Type, from url: URL, decoder: JSONDecoder = FlexarrClient.decoder) -> Loaded<Element> {
        guard let data = try? Data(contentsOf: url) else { return Loaded(items: [], unreadable: 0, keptAt: nil) }
        let entries = try? decoder.decode([Entry<Element>].self, from: data)
        let items = entries?.compactMap(\.value) ?? []
        let unreadable = entries.map { $0.count - items.count } ?? 1
        guard unreadable > 0 else { return Loaded(items: items, unreadable: 0, keptAt: nil) }
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let copy = url.deletingPathExtension().appendingPathExtension("unreadable-\(stamp).json")
        let kept = (try? FileManager.default.copyItem(at: url, to: copy)) != nil ? copy : nil
        return Loaded(items: items, unreadable: unreadable, keptAt: kept)
    }

    private struct Entry<Value: Decodable>: Decodable {
        let value: Value?

        init(from decoder: Decoder) throws {
            value = try? Value(from: decoder)
        }
    }
}

extension SavedList {
    /// Writes a saved list to its file away from the thread that changed it, and only the newest: a run of changes (a
    /// playlist's tracks arriving one after another) becomes one write every `interval` rather than one each, and
    /// turning a long list into a file never holds up the screen. The first change after a quiet spell is written
    /// straight away, and so is one that can't wait (`rightAway`). `flush` writes whatever is waiting before it
    /// returns, for when the app is about to be suspended or closed.
    public final class Writer<Element: Encodable & Sendable>: @unchecked Sendable {
        private let url: URL
        private let interval: TimeInterval
        private let encoder: JSONEncoder
        private let queue = DispatchQueue(label: "io.github.flexarr.saved-list", qos: .utility)
        private let lock = NSLock()
        /// The newest list not yet written.
        private var waiting: [Element]?
        /// A write is on its way for whatever is waiting by then.
        private var scheduled = false
        private var lastWrite = Date.distantPast
        private var written = 0

        /// How many times the file has been written (for tests).
        var writes: Int { lock.withLock { written } }

        /// `encoder` is this writer's alone from here on.
        public init(to url: URL, atMostEvery interval: TimeInterval = 2, encoder: JSONEncoder = JSONEncoder()) {
            self.url = url
            self.interval = interval
            self.encoder = encoder
        }

        /// Takes the list as it is now, to be written shortly; one handed over before that replaces it. `rightAway`
        /// for a change the file mustn't be behind on for even a moment (a download's file arriving or going): it's
        /// written as soon as the write under way, if any, is done.
        public func save(_ items: [Element], rightAway: Bool = false) {
            let delay: TimeInterval? = lock.withLock {
                waiting = items
                if rightAway {
                    scheduled = true
                    return 0
                }
                if scheduled { return nil }
                scheduled = true
                return max(0, interval - Date().timeIntervalSince(lastWrite))
            }
            guard let delay else { return }
            queue.asyncAfter(deadline: .now() + delay) { self.writeWaiting() }
        }

        /// Writes what's waiting (after any write under way) before returning.
        public func flush() {
            queue.sync { writeWaiting() }
        }

        /// Only ever on `queue`, so writes land in the order they were asked for.
        private func writeWaiting() {
            let items: [Element]? = lock.withLock {
                defer {
                    waiting = nil
                    scheduled = false
                }
                if waiting != nil { lastWrite = Date() }
                return waiting
            }
            guard let items else { return }
            if let data = try? encoder.encode(items) { try? data.write(to: url, options: .atomic) }
            lock.withLock { written += 1 }
        }
    }
}
