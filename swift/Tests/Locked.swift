// SPDX-License-Identifier: MIT
import Foundation

/// Thread-safe mutable box used by tests to capture a value set inside a
/// `@Sendable` callback without mutating a captured `var`.
///
/// Mutating a captured `var` from a closure that runs in another concurrency
/// domain is a data race: the Swift 6 language mode rejects it as a hard error
/// (`#SendableClosureCaptures`), while Swift 5 mode only warns. Routing the
/// capture through this lock-guarded box makes the pattern correct under every
/// toolchain and language mode.
final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var _value: Value

    init(_ value: Value) { _value = value }

    var value: Value {
        get { lock.lock(); defer { lock.unlock() }; return _value }
        set { lock.lock(); defer { lock.unlock() }; _value = newValue }
    }
}
