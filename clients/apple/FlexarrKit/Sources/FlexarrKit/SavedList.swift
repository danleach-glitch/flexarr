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
