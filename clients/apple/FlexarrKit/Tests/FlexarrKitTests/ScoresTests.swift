import Foundation
import Testing
@testable import FlexarrKit

/// IMDb and Rotten Tomatoes beside their marks, from new and older servers.
struct ScoresTests {
    private func movie(imdb: Double? = nil, rt: RottenTomatoes? = nil) throws -> ItemSummary {
        var item = try Fixture.decode(ItemDetail.self, "item-movie").item
        item.imdbRating = imdb
        item.rottenTomatoes = rt
        return item
    }

    @Test func aNewServersScores() throws {
        let scores = try movie(imdb: 8.1, rt: RottenTomatoes(score: 96, state: .certifiedFresh)).scores
        #expect(scores.imdbText == "8.1")
        #expect(scores.rottenTomatoes == RottenTomatoes(score: 96, state: .certifiedFresh))
        #expect(scores.rottenTomatoesLabel == "Rotten Tomatoes 96%, Certified Fresh")
        #expect(scores.imdbLabel == "IMDb 8.1 out of 10")
    }

    @Test func anOlderServersCriticsScore_GetsATomatoFromTheNumber() throws {
        let item = try movie() // the fixture has criticRating 96 and no rottenTomatoes
        #expect(item.scores.rottenTomatoes == RottenTomatoes(score: 96, state: .fresh))
        #expect(item.scores.imdb == nil)
        #expect(item.facts.contains("7.4")) // without IMDb, the plain score stays beside the tomato
        var withImdb = item
        withImdb.imdbRating = 7.4
        #expect(!withImdb.facts.contains("7.4")) // IMDb's mark takes its place
    }

    @Test func anUnknownTomato_FollowsTheScore() throws {
        let json = Data(#"{"score": 41, "state": "SomethingNew"}"#.utf8)
        let rt = try JSONDecoder().decode(RottenTomatoes.self, from: json)
        #expect(rt.state == .unknown)
        #expect(Scores(imdb: nil, rottenTomatoes: rt).rottenTomatoes?.state == .rotten)
        #expect(Scores(imdb: nil, rottenTomatoes: RottenTomatoes(score: 60, state: .unknown)).rottenTomatoes?.state == .fresh)
    }

    @Test func noScores_KeepsThePlainOne() throws {
        let json = try Fixture.data("item-movie")
        var object = try #require(JSONSerialization.jsonObject(with: json) as? [String: Any])
        var item = try #require(object["item"] as? [String: Any])
        item["criticRating"] = NSNull()
        object["item"] = item
        let detail = try FlexarrClient.decoder.decode(ItemDetail.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(detail.item.scores.isEmpty)
        #expect(detail.item.facts.contains("7.4"))
        #expect(!detail.item.facts(scores: false).contains("7.4"))
    }

    @Test func discoverHasTheSameScores() throws {
        var title = try Fixture.decode(ExternalDetail.self, "requests-title")
        title.rottenTomatoesState = .certifiedFresh
        let critics = try #require(title.criticScore)
        #expect(title.scores.rottenTomatoes == RottenTomatoes(score: critics, state: .certifiedFresh))
        #expect(title.scores.imdb == title.imdbRating)
    }
}
