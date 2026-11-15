import Foundation
import Testing
@testable import FlexarrKit

/// An address shows it holds the app's key before it's sent it over plain HTTP (see `ServerProof`).
struct ServerProofTests {
    private static let challenge = "00112233445566778899aabbccddeeff"

    private func memory() -> UserDefaults {
        let name = "ServerProofTests-\(UUID().uuidString)"
        return UserDefaults(suiteName: name)!
    }

    /// The same numbers as the server's own test (ServerProofTests in Flexarr.Api.Tests), so both work it out alike.
    @Test func worksOutTheSameAnswersAsTheServer() {
        let hash = ServerProof.hash(of: "test-key")

        #expect(hash == "62af8704764faf8ea82fc61ce9c4c3908b6cb97d463a634e9e587d7c885db0ef")
        #expect(ServerProof.keyId(hash) == "3de99eba31a42ce3d62608d3d0dc14263f03df662a95478c87e6f6bba7dcba2f")
        #expect(ServerProof.answer(keyHash: hash, serverId: "server-1", challenge: Self.challenge)
            == "7991230abbbdddd2888885e97b6dad0651507d6da3f6f3467d198caaa5378e01")
    }

    @Test func challengesAreFreshEachTime() {
        let first = ServerProof.challenge()

        #expect(first.count == 32)
        #expect(first != ServerProof.challenge())
    }

    @Test func theRightAnswerIsBelieved_AndFromThenOnNoAnswerIsnt() {
        let memory = memory()
        let right = ServerProof.answer(keyHash: ServerProof.hash(of: "test-key"), serverId: "server-1", challenge: Self.challenge)

        // A server from before gives none: its id is all there is to go on.
        #expect(ServerProof.accepts(id: "server-1", proof: nil, expected: "server-1", key: "test-key", challenge: Self.challenge, memory: memory))
        #expect(ServerProof.accepts(id: "server-1", proof: right, expected: "server-1", key: "test-key", challenge: Self.challenge, memory: memory))
        // Something at the same address elsewhere that gives the id, but no answer, once the server has given one.
        #expect(!ServerProof.accepts(id: "server-1", proof: nil, expected: "server-1", key: "test-key", challenge: Self.challenge, memory: memory))
    }

    @Test func anAnswerToAnotherChallenge_OrFromAnotherServer_OrForAnotherKey_IsntBelieved() {
        let memory = memory()
        let hash = ServerProof.hash(of: "test-key")
        let replayed = ServerProof.answer(keyHash: hash, serverId: "server-1", challenge: "ffeeddccbbaa99887766554433221100")
        let otherServer = ServerProof.answer(keyHash: hash, serverId: "server-2", challenge: Self.challenge)
        let otherKey = ServerProof.answer(keyHash: ServerProof.hash(of: "another-key"), serverId: "server-1", challenge: Self.challenge)

        for proof in [replayed, otherServer, otherKey, "nonsense"] {
            #expect(!ServerProof.accepts(id: "server-1", proof: proof, expected: "server-1", key: "test-key", challenge: Self.challenge,
                                         memory: memory))
        }
        #expect(!ServerProof.accepts(id: "server-2", proof: nil, expected: "server-1", key: "test-key", challenge: Self.challenge, memory: memory))
    }

    @Test func withNoKeyToSend_TheIdIsEnough() {
        #expect(ServerProof.accepts(id: "server-1", proof: nil, expected: "server-1", key: nil, challenge: Self.challenge, memory: memory()))
    }
}
