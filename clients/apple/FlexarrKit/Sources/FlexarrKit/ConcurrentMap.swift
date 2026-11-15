import Foundation

extension Array where Element: Sendable {
    /// `transform` over every element, at most `limit` at a time, the results in the elements' order. The first error
    /// thrown cancels the rest and is rethrown. A show's thirty seasons fetched four at a time instead of one after
    /// another, say.
    public func concurrentMap<Result: Sendable>(
        limit: Int, _ transform: @escaping @Sendable (Element) async throws -> Result
    ) async throws -> [Result] {
        guard !isEmpty else { return [] }
        let width = Swift.max(1, limit)
        return try await withThrowingTaskGroup(of: (Int, Result).self) { group in
            var results = [Result?](repeating: nil, count: count)
            var next = 0
            func addNext() {
                let index = next, element = self[index]
                group.addTask { (index, try await transform(element)) }
                next += 1
            }
            while next < Swift.min(width, count) { addNext() }
            while let (index, result) = try await group.next() {
                results[index] = result
                if next < count { addNext() }
            }
            return results.map { $0! }
        }
    }
}
