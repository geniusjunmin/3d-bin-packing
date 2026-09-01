using BinPacking.Web.Algorithms;
using BinPacking.Web.Models;
using BinPacking.Web.Services;

namespace BinPacking.Tests;

public sealed class PackingBenchmarkTests
{
    [Fact]
    public void Pack_DeterministicScenario_ProducesRepeatablePlacementAndDiagnostics()
    {
        var algorithm = new HybridPackingAlgorithm(PackingAlgorithmOptions.Fast);
        var box = new BoxType { Id = Guid.Parse("00000000-0000-0000-0000-000000000100"), Name = "Benchmark", Length = 400, Width = 300, Height = 240 };
        var typeId = Guid.Parse("00000000-0000-0000-0000-000000000200");
        var items = Enumerable.Range(1, 20).Select(index => new PackingItemUnit(
            new Guid(index, 0, 0, new byte[8]), typeId, "Unit", index,
            100, 80, 60, 1, true, "#60A5FA")).ToArray();

        var first = algorithm.Pack(box, items);
        var second = algorithm.Pack(box, items);

        Assert.Equal(first.PackedItems, second.PackedItems);
        Assert.Equal(first.UnpackedItems, second.UnpackedItems);
        Assert.True(first.Diagnostics.CandidateEvaluations > 0);
        Assert.True(first.Diagnostics.ExtremePointCount > 0);
        Assert.True(first.Diagnostics.CalculationTimeMs >= 0);
    }

    [Fact]
    public void Pack_EmsWasteTrap_PacksMoreVolumeThanBaselineWithoutBreakingConstraints()
    {
        var box = new BoxType { Name = "EMS trap", Length = 600, Width = 500, Height = 400 };
        var specs = new[]
        {
            (170, 250, 210), (230, 250, 50), (120, 80, 80), (260, 110, 170), (240, 270, 90)
        };
        var items = Enumerable.Range(0, 30).Select(index =>
        {
            var spec = specs[index % specs.Length];
            return new PackingItemUnit(
                StableGuid(9_900 + index), StableGuid(10_990 + index % specs.Length), $"Type {index % specs.Length}", index + 1,
                spec.Item1, spec.Item2, spec.Item3, 1, true, "#60A5FA");
        }).ToArray();

        var baseline = new ExtremePointPackingAlgorithm().Pack(box, items);
        // Quality assertions must not depend on the speed of the CI host. The
        // production Balanced budget remains 250 ms; this fixture gives the
        // bounded search enough time to complete the same depth on all runners.
        var benchmarkOptions = PackingAlgorithmOptions.Balanced with { TimeBudgetMs = 10_000 };
        var optimized = new HybridPackingAlgorithm(benchmarkOptions).Pack(box, items);

        Assert.True(optimized.PackedVolume > baseline.PackedVolume,
            $"Expected EMS score to beat {baseline.PackedVolume}, actual {optimized.PackedVolume}.");
        Assert.True(optimized.PackedItems.Count > baseline.PackedItems.Count);
        Assert.All(optimized.PackedItems, item =>
        {
            Assert.True(item.X >= 0 && item.Y >= 0 && item.Z >= 0);
            Assert.True(item.X + item.Length <= box.Length);
            Assert.True(item.Y + item.Width <= box.Width);
            Assert.True(item.Z + item.Height <= box.Height);
            Assert.InRange(item.SupportPercent, 90, 100);
        });
    }

    [Fact]
    public void BoxPlanOptimizer_WhenImmediateItemCountIsATrap_UsesFewerBoxesThanGreedyMixedPlan()
    {
        var flat = new BoxType { Id = StableGuid(20_001), Name = "Flat", Length = 600, Width = 400, Height = 200 };
        var cube = new BoxType { Id = StableGuid(20_002), Name = "Cube", Length = 400, Width = 400, Height = 400, MaxWeightKg = 10 };
        var tallType = StableGuid(20_101);
        var smallType = StableGuid(20_102);
        var items = new List<PackingItemUnit>();
        for (var index = 0; index < 2; index++)
            items.Add(new PackingItemUnit(StableGuid(21_000 + index), tallType, "Tall", index + 1, 200, 400, 400, 6, false, "#60A5FA"));
        for (var index = 0; index < 8; index++)
            items.Add(new PackingItemUnit(StableGuid(22_000 + index), smallType, "Small", index + 1, 200, 200, 190, 1, false, "#60A5FA"));

        var algorithm = new ExtremePointPackingAlgorithm();
        var greedy = BuildGreedyMixedPlan(algorithm, [flat, cube], items);
        var optimized = new BoxPlanOptimizer(algorithm).BuildPlan([flat, cube], items);

        Assert.NotNull(optimized);
        var flatAttempt = algorithm.Pack(flat, items);
        var cubeAttempt = algorithm.Pack(cube, items);
        Assert.True(optimized.Count < greedy.Count,
            $"Expected lookahead < {greedy.Count}, actual {optimized.Count}; flat={flatAttempt.PackedItems.Count}, cube={cubeAttempt.PackedItems.Count}; optimized={string.Join(',', optimized.Select(step => $"{step.Box.Name}:{step.PackedItems.Count}"))}.");
        Assert.Equal(2, optimized.Count);
    }

    private static List<PackingAttempt> BuildGreedyMixedPlan(IPackingAlgorithm algorithm, IReadOnlyList<BoxType> boxes, IReadOnlyList<PackingItemUnit> source)
    {
        var remaining = source.ToList();
        var plan = new List<PackingAttempt>();
        while (remaining.Count > 0)
        {
            var candidate = boxes.Select(box => algorithm.Pack(box, remaining))
                .Where(attempt => attempt.PackedItems.Count > 0)
                .OrderByDescending(attempt => attempt.PackedItems.Count)
                .ThenByDescending(attempt => attempt.PackedVolume)
                .ThenBy(attempt => attempt.Box.Volume)
                .First();
            plan.Add(candidate);
            var ids = candidate.PackedItems.Select(item => item.InstanceId).ToHashSet();
            remaining.RemoveAll(item => ids.Contains(item.InstanceId));
        }
        return plan;
    }

    private static Guid StableGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        BitConverter.TryWriteBytes(bytes[8..], value * 486_187_739L);
        return new Guid(bytes);
    }
}
