// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace AetherNet.Soak.Tests;

/// <summary>
/// Base class for the soak suite. Provides:
/// <list type="bullet">
///   <item>Iteration count resolution: env var <c>AETHERNET_SOAK_ITERATIONS</c>
///     overrides the per-test default. CI runs that want long-form soak
///     bumps the env var, devs running locally get the lighter default.</item>
///   <item><see cref="MeasureMemoryGrowthAsync"/> — wraps a workload in
///     before/after GC stats and returns net allocated bytes plus per-iteration
///     average. Forces a full collection on both ends so the measurement is
///     resilient to background GC drift.</item>
///   <item><see cref="AssertEventuallyUnreachable"/> — verifies that an
///     allocation captured by <see cref="WeakReference"/> is actually
///     collectable after the workload completes (tests that the protocol
///     code did not retain a hidden strong reference somewhere).</item>
/// </list>
///
/// Soak tests are tagged <c>[Trait("Category", "Soak")]</c> so they're
/// filterable. Default <c>dotnet test</c> on the main test project skips
/// them; running this assembly explicitly via
/// <c>dotnet test tests/AetherNet.Soak.Tests/...</c> picks them up.
/// </summary>
public abstract class SoakTestBase
{
    /// <summary>
    /// Default iteration budget. Aimed at &lt; 30 s on a developer laptop;
    /// CI bumps via <c>AETHERNET_SOAK_ITERATIONS</c> for true soak runs.
    /// </summary>
    public const int DefaultIterations = 10_000;

    /// <summary>
    /// Resolves the iteration count from the env var if set, else
    /// <paramref name="defaultIterations"/>. Negative or unparseable env
    /// values fall back to the default.
    /// </summary>
    public static int ResolveIterations(int defaultIterations = DefaultIterations)
    {
        var raw = Environment.GetEnvironmentVariable("AETHERNET_SOAK_ITERATIONS");
        if (string.IsNullOrEmpty(raw)) return defaultIterations;
        return int.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : defaultIterations;
    }

    /// <summary>
    /// Memory measurement summary returned from <see cref="MeasureMemoryGrowthAsync"/>.
    /// All bytes counts are post-GC managed-heap totals.
    /// </summary>
    /// <param name="BeforeBytes">Total managed bytes before the workload.</param>
    /// <param name="AfterBytes">Total managed bytes after the workload.</param>
    /// <param name="NetGrowthBytes">After minus before. Can be negative if
    ///   the workload happened to shake loose pre-existing garbage.</param>
    /// <param name="PerIterationBytes">Net growth divided by iteration count.</param>
    /// <param name="Gen0Collections">Gen-0 GC count delta during the workload.</param>
    /// <param name="Gen1Collections">Gen-1 GC count delta during the workload.</param>
    /// <param name="Gen2Collections">Gen-2 GC count delta during the workload.</param>
    /// <param name="ElapsedMs">Wall-clock duration of the workload in milliseconds.</param>
    public sealed record MemoryGrowthReport(
        long BeforeBytes,
        long AfterBytes,
        long NetGrowthBytes,
        double PerIterationBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long ElapsedMs)
    {
        public double IterationsPerSecond(int iterations)
            => ElapsedMs > 0 ? iterations * 1_000.0 / ElapsedMs : 0;
    }

    /// <summary>
    /// Runs <paramref name="workload"/> for <paramref name="iterations"/>
    /// iterations and returns a memory-growth report. Forces a full GC on
    /// both ends so the measurement is comparable run-to-run.
    /// </summary>
    /// <param name="workload">The per-iteration async workload. Receives the
    ///   zero-based iteration index.</param>
    /// <param name="iterations">How many times to run <paramref name="workload"/>.</param>
    /// <param name="warmupIterations">Optional warmup pass before the
    ///   measurement window. Used to amortize JIT and one-shot allocations
    ///   (initial pre-key pool fill, etc.) so they don't pollute the
    ///   per-iteration average. Defaults to 0.</param>
    public static async Task<MemoryGrowthReport> MeasureMemoryGrowthAsync(
        Func<int, Task> workload,
        int iterations,
        int warmupIterations = 0)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), "iterations must be >= 1");

        for (var i = 0; i < warmupIterations; i++)
            await workload(i).ConfigureAwait(false);

        ForceFullCollect();
        var beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var beforeGen0 = GC.CollectionCount(0);
        var beforeGen1 = GC.CollectionCount(1);
        var beforeGen2 = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            await workload(i).ConfigureAwait(false);
        sw.Stop();

        ForceFullCollect();
        var afterBytes = GC.GetTotalMemory(forceFullCollection: true);
        var afterGen0 = GC.CollectionCount(0);
        var afterGen1 = GC.CollectionCount(1);
        var afterGen2 = GC.CollectionCount(2);

        return new MemoryGrowthReport(
            BeforeBytes: beforeBytes,
            AfterBytes: afterBytes,
            NetGrowthBytes: afterBytes - beforeBytes,
            PerIterationBytes: (afterBytes - beforeBytes) / (double)iterations,
            Gen0Collections: afterGen0 - beforeGen0,
            Gen1Collections: afterGen1 - beforeGen1,
            Gen2Collections: afterGen2 - beforeGen2,
            ElapsedMs: sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Synchronous overload for workloads that are fully sync.
    /// </summary>
    public static MemoryGrowthReport MeasureMemoryGrowth(
        Action<int> workload,
        int iterations,
        int warmupIterations = 0)
    {
        ArgumentNullException.ThrowIfNull(workload);
        return MeasureMemoryGrowthAsync(
            i => { workload(i); return Task.CompletedTask; },
            iterations,
            warmupIterations).GetAwaiter().GetResult();
    }

    /// <summary>
    /// After the workload has completed, asserts that the supplied
    /// <see cref="WeakReference"/> is actually collectable. Forces a sequence
    /// of full GCs and rechecks. Used to verify that protocol services do
    /// not retain hidden strong references to per-iteration state (sessions,
    /// payloads, message keys).
    /// </summary>
    /// <param name="weakRef">The reference to verify.</param>
    /// <param name="description">Free-form description for the assertion failure.</param>
    public static void AssertEventuallyUnreachable(WeakReference weakRef, string description)
    {
        ArgumentNullException.ThrowIfNull(weakRef);

        for (var attempt = 0; attempt < 5 && weakRef.IsAlive; attempt++)
            ForceFullCollect();

        if (weakRef.IsAlive)
            throw new Xunit.Sdk.XunitException(
                $"Expected {description} to be unreachable after the workload, but a strong " +
                "reference is still being retained somewhere — likely a memory leak in the protocol code.");
    }

    /// <summary>
    /// Forces a deterministic full collection across all generations and
    /// drains the finalizer queue. Cycles twice so finalizers that schedule
    /// further work get cleaned up too.
    /// </summary>
    public static void ForceFullCollect()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
    }

    /// <summary>
    /// Helper for emitting throughput / memory numbers to the test runner's
    /// output without taking a hard dependency on
    /// <see cref="Xunit.Abstractions.ITestOutputHelper"/> (writes to stderr
    /// when no output sink is wired). Used so soak runs surface their
    /// throughput without polluting standard test output formatters.
    /// </summary>
    protected static void WriteSummary(string title, MemoryGrowthReport report, int iterations)
    {
        var line = $"[soak] {title}: " +
            $"iters={iterations}, " +
            $"throughput={report.IterationsPerSecond(iterations):F1}/s, " +
            $"net_growth={report.NetGrowthBytes:N0}B " +
            $"({report.PerIterationBytes:F1}B/iter), " +
            $"gc=[g0:{report.Gen0Collections} g1:{report.Gen1Collections} g2:{report.Gen2Collections}], " +
            $"elapsed={report.ElapsedMs}ms";
        Console.WriteLine(line);
    }
}
