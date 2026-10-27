import FlexarrKit
import Foundation
import UserNotifications

/// Opens what the server sealed (the relay only ever carries "Something new is ready"): the real title and text, the
/// buttons, the grouping and the artwork. Whatever can't be opened in time arrives as it came.
final class NotificationService: UNNotificationServiceExtension, @unchecked Sendable {
    private let lock = NSLock()
    private var handler: ((UNNotificationContent) -> Void)?
    /// The best there is so far: what arrived, then the opened text, then that with its artwork.
    private var best: UNNotificationContent?
    private var download: URLSessionDownloadTask?

    override func didReceive(_ request: UNNotificationRequest, withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void) {
        lock.withLock {
            handler = contentHandler
            best = request.content
        }
        guard let opened = Self.open(request.content) else { return deliver(request.content) }
        lock.withLock { best = opened.content }
        guard let image = opened.image, image.scheme == "https" else { return deliver(opened.content) }

        let task = URLSession.shared.downloadTask(with: URLRequest(url: image, timeoutInterval: 10)) { [self] location, response, _ in
            let opened = lock.withLock { best?.mutableCopy() as? UNMutableNotificationContent }
            guard let opened else { return }
            if let location, (response as? HTTPURLResponse)?.statusCode == 200,
               let attachment = Self.attachment(from: location, named: image) {
                opened.attachments = [attachment]
            }
            deliver(opened)
        }
        lock.withLock { download = task }
        task.resume()
    }

    /// Out of time (a slow artwork server): the text without the picture.
    override func serviceExtensionTimeWillExpire() {
        let content = lock.withLock { () -> UNNotificationContent? in
            download?.cancel()
            return best
        }
        if let content { deliver(content) }
    }

    /// Hands the content over, once.
    private func deliver(_ content: UNNotificationContent) {
        let handler = lock.withLock { () -> ((UNNotificationContent) -> Void)? in
            defer { self.handler = nil }
            return self.handler
        }
        handler?(content)
    }

    private static func open(_ content: UNNotificationContent) -> (content: UNMutableNotificationContent, image: URL?)? {
        let group = Bundle.main.object(forInfoDictionaryKey: "FlexarrAppGroup") as? String
        guard let sealed = content.userInfo["e"] as? String, let key = NotificationKey.load(accessGroup: group),
              let payload = try? PushPayload.open(sealed, key: key),
              let opened = content.mutableCopy() as? UNMutableNotificationContent else { return nil }
        if !payload.title.isEmpty { opened.title = payload.title }
        opened.body = payload.body
        if let category = payload.category { opened.categoryIdentifier = category }
        if let thread = payload.thread { opened.threadIdentifier = thread }
        // What the app needs when it's tapped or a button is pressed (`item`, `play`, `podcast`, `episode`).
        var info = content.userInfo
        info.removeValue(forKey: "e")
        for (key, value) in payload.info { info[key] = value }
        opened.userInfo = info
        return (opened, payload.image)
    }

    /// The downloaded artwork, moved where the system can take it, named so it knows what kind of picture it is.
    private static func attachment(from location: URL, named image: URL) -> UNNotificationAttachment? {
        let known = ["jpg", "jpeg", "png", "gif", "heic"]
        let ext = known.contains(image.pathExtension.lowercased()) ? image.pathExtension.lowercased() : "jpg"
        let file = FileManager.default.temporaryDirectory.appending(path: "\(UUID().uuidString).\(ext)")
        do {
            try FileManager.default.moveItem(at: location, to: file)
            return try UNNotificationAttachment(identifier: "artwork", url: file)
        } catch {
            return nil
        }
    }
}
