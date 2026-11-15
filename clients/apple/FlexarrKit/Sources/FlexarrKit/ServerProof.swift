import CryptoKit
import Foundation

/// How an address shows it's the app's own server before it's sent the sign-in over plain HTTP. Its id alone isn't
/// enough: anyone may ask a server for its id, and something else at the same address on another network could give
/// it too. So the app sends a fresh random challenge and which key it holds (`keyId`, from which the key can't be
/// worked out), and the server answers with what only a holder of that key can work out (`answer`). Servers before
/// 0.67 give no answer; once the server has answered on this device, an address that doesn't is never trusted again.
enum ServerProof {
    /// The SHA-256 of a key, as both sides start from (lowercase hex).
    static func hash(of key: String) -> String { hex(SHA256.hash(data: Data(key.utf8))) }

    /// What the app calls the key it holds, given its hash.
    static func keyId(_ keyHash: String) -> String { hex(SHA256.hash(data: Data("flexarr key id|\(keyHash)".utf8))) }

    /// The answer to `challenge` from the server with id `serverId` that holds the key.
    static func answer(keyHash: String, serverId: String, challenge: String) -> String {
        let code = HMAC<SHA256>.authenticationCode(for: Data("flexarr proof|\(serverId)|\(challenge)".utf8),
                                                   using: SymmetricKey(data: Data(keyHash.utf8)))
        return Data(code).map { String(format: "%02x", $0) }.joined()
    }

    /// A fresh challenge: 16 random bytes, in hex.
    static func challenge() -> String {
        var generator = SystemRandomNumberGenerator()
        return (0..<16).map { _ in String(format: "%02x", UInt8.random(in: .min ... .max, using: &generator)) }.joined()
    }

    /// Where it's noted that the server with this id has answered on this device.
    static func memoryKey(_ serverId: String) -> String { "io.github.flexarr.server.answersForKey.\(serverId)" }

    /// Whether what answered is the server expected: it gave `expected` for its id and, when the app has a key to
    /// send (`key`), the right answer to `challenge`, or none at all if this server has never given one here (a server
    /// from before). A right answer is remembered in `memory`, so from then on an address without one isn't believed.
    static func accepts(id: String?, proof: String?, expected: String, key: String?, challenge: String, memory: UserDefaults) -> Bool {
        guard id == expected else { return false }
        guard let key, !key.isEmpty else { return true }
        guard let proof else { return !memory.bool(forKey: memoryKey(expected)) }
        guard proof == answer(keyHash: hash(of: key), serverId: expected, challenge: challenge) else { return false }
        memory.set(true, forKey: memoryKey(expected))
        return true
    }

    private static func hex(_ digest: SHA256.Digest) -> String { digest.map { String(format: "%02x", $0) }.joined() }
}
