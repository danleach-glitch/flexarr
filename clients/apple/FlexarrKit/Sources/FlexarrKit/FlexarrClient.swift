import Foundation

public enum FlexarrClientError: Error, Equatable {
    case unauthorized
    case http(status: Int)
    case invalidResponse
}

/// Talks to a Flexarr server's `/api/v1` REST API. Shared by the tvOS, iOS and macOS apps.
public struct FlexarrClient: Sendable {
    public let baseURL: URL
    public let apiKey: String?
    private let session: URLSession

    public init(baseURL: URL, apiKey: String? = nil, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.apiKey = apiKey
        self.session = session
    }

    public func systemStatus() async throws -> SystemStatus {
        try await get("system/status")
    }

    func get<T: Decodable>(_ path: String) async throws -> T {
        var request = URLRequest(url: baseURL.appending(path: "api/v1/\(path)"))
        if let apiKey {
            request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        }

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw FlexarrClientError.invalidResponse }

        switch http.statusCode {
        case 200..<300:
            return try Self.decoder.decode(T.self, from: data)
        case 401:
            throw FlexarrClientError.unauthorized
        default:
            throw FlexarrClientError.http(status: http.statusCode)
        }
    }

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            // .NET emits fractional seconds with up to 7 digits; ISO8601DateFormatter handles both forms.
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = formatter.date(from: value) { return date }
            formatter.formatOptions = [.withInternetDateTime]
            if let date = formatter.date(from: value) { return date }
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Invalid date: \(value)"))
        }
        return decoder
    }()
}
