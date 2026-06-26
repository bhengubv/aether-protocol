// SPDX-License-Identifier: MIT
// NOTE: CI on Linux is the verification gate.

import Foundation

/// Codable property wrapper that serialises a `UUID` as a **lowercase** RFC4122
/// string and parses it case-insensitively on decode.
///
/// Foundation's default `UUID` Codable conformance emits the *uppercase*
/// `uuidString`, which diverges from every other AetherNet SDK (Go / C# / Rust /
/// Python / Kotlin / TypeScript / C all emit lowercase) and from the
/// cross-language wire fixtures. Annotating the JSON signalling structs' id
/// fields with this wrapper keeps the Swift payloads byte-identical to the other
/// languages without touching any construction or access site — the wrapped
/// value stays a plain `UUID`.
@propertyWrapper
struct LowercaseUUIDCoding: Codable, Sendable, Equatable {
    var wrappedValue: UUID

    init(wrappedValue: UUID) { self.wrappedValue = wrappedValue }

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let value = UUID(uuidString: raw) else {
            throw DecodingError.dataCorrupted(
                .init(codingPath: decoder.codingPath,
                      debugDescription: "Invalid UUID string: \(raw)")
            )
        }
        wrappedValue = value
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wrappedValue.uuidString.lowercased())
    }
}
