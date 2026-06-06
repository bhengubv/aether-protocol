// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Running;

namespace AetherMesh.Benchmarks;

/// <summary>
/// Entry point for the Aether benchmark suite.
///
/// Uses <see cref="BenchmarkSwitcher"/> so individual benchmark classes or
/// methods can be selected from the command line without recompiling. Common
/// invocations:
///
///   dotnet run -c Release --project bench/AetherMesh.Benchmarks -- --filter "*"
///   dotnet run -c Release --project bench/AetherMesh.Benchmarks -- --filter "*PacketSerializer*"
///   dotnet run -c Release --project bench/AetherMesh.Benchmarks -- --list flat
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        var summaries = switcher.Run(args);

        // Non-zero exit when no benchmark class matched the filter (e.g. typo).
        // Empty result set is the only consistent failure signal across BDN versions.
        return summaries is null || !summaries.Any() ? 1 : 0;
    }
}
