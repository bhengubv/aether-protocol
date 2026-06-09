// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language wire-format verifier for the `aether://` URI scheme. Reads
/// `../../tests/cross-language/uri-fixtures.json` (walking up from CWD) and asserts
/// that this Swift SDK parses every valid input to the same canonical components and
/// re-emits the same canonical string — byte-equal with the other seven language SDKs.
final class CrossLanguageFixtureTests: XCTestCase {

    // MARK: - Fixture types

    private struct ValidFixture: Decodable {
        let name: String
        let input: String
        let canonical: String
        let authority: String
        let path: String
        let handlerName: String
        let pathSegments: [String]
        let query: [String: String]
        let fragment: String
    }

    private struct InvalidFixture: Decodable {
        let name: String
        let input: String
    }

    private struct ManifestEntry: Decodable {
        let handlerName: String
        let pathTemplate: String
    }

    private struct ManifestMatch: Decodable {
        let input: String
        let matched: Bool
        let handlerIndex: Int?
        let captures: [String: String]?
    }

    private struct ManifestFixture: Decodable {
        let appId: String
        let handlers: [ManifestEntry]
        let matches: [ManifestMatch]
    }

    private struct UriFixtures: Decodable {
        let valid: [ValidFixture]
        let invalid: [InvalidFixture]
        let manifest: ManifestFixture
    }

    // MARK: - Fixture loader

    private func loadFixtures() throws -> UriFixtures {
        var url = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        for _ in 0..<8 {
            let candidate = url
                .appendingPathComponent("tests")
                .appendingPathComponent("cross-language")
                .appendingPathComponent("uri-fixtures.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(UriFixtures.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate tests/cross-language/uri-fixtures.json")
        // Unreachable; XCTFail does not stop execution.
        throw AetherUri.AetherUriError.message("missing fixtures")
    }

    // MARK: - Valid corpus

    func testAllValidFixturesParseToExpectedComponents() throws {
        let fixtures = try loadFixtures()
        for fixture in fixtures.valid {
            let uri = try AetherUri.parse(fixture.input)
            XCTAssertEqual(uri.authority, fixture.authority,
                "[\(fixture.name)] authority mismatch")
            XCTAssertEqual(uri.path, fixture.path,
                "[\(fixture.name)] path mismatch")
            XCTAssertEqual(uri.handlerName, fixture.handlerName,
                "[\(fixture.name)] handlerName mismatch")
            XCTAssertEqual(uri.pathSegments, fixture.pathSegments,
                "[\(fixture.name)] pathSegments mismatch")
            XCTAssertEqual(uri.query, fixture.query,
                "[\(fixture.name)] query mismatch")
            XCTAssertEqual(uri.fragment, fixture.fragment,
                "[\(fixture.name)] fragment mismatch")
        }
    }

    func testAllValidFixturesEmitCanonicalString() throws {
        let fixtures = try loadFixtures()
        for fixture in fixtures.valid {
            let uri = try AetherUri.parse(fixture.input)
            XCTAssertEqual(uri.description, fixture.canonical,
                "[\(fixture.name)] canonical mismatch (got \(uri.description), expected \(fixture.canonical))")
        }
    }

    func testCanonicalIsIdempotent() throws {
        let fixtures = try loadFixtures()
        for fixture in fixtures.valid {
            let first = try AetherUri.parse(fixture.input)
            let second = try AetherUri.parse(first.description)
            XCTAssertEqual(first.description, second.description,
                "[\(fixture.name)] canonical not idempotent")
        }
    }

    // MARK: - Invalid corpus

    func testAllInvalidFixturesAreRejected() throws {
        let fixtures = try loadFixtures()
        for fixture in fixtures.invalid {
            let result = AetherUri.tryParse(fixture.input)
            switch result {
            case .success:
                XCTFail("[\(fixture.name)] expected failure for '\(fixture.input)'")
            case .failure:
                continue
            }
        }
    }

    // MARK: - Manifest corpus

    func testManifestResolutionMatchesFixture() throws {
        let fixtures = try loadFixtures()
        let manifestFixture = fixtures.manifest
        let descriptors = manifestFixture.handlers.map { entry in
            HandlerDescriptor(name: entry.handlerName, pathTemplate: entry.pathTemplate)
        }
        let manifest = HandlerManifest(appId: manifestFixture.appId, handlers: descriptors)

        for match in manifestFixture.matches {
            let uri = try AetherUri.parse(match.input)
            let resolved = manifest.resolve(uri)
            if match.matched {
                XCTAssertNotNil(resolved,
                    "[\(match.input)] expected match but got nil")
                if let resolved {
                    if let expectedIndex = match.handlerIndex {
                        XCTAssertEqual(resolved.0, descriptors[expectedIndex],
                            "[\(match.input)] handler index mismatch")
                    }
                    if let expectedCaptures = match.captures {
                        XCTAssertEqual(resolved.1, expectedCaptures,
                            "[\(match.input)] captures mismatch")
                    }
                }
            } else {
                XCTAssertNil(resolved,
                    "[\(match.input)] expected no match but got \(String(describing: resolved))")
            }
        }
    }
}
