using System.Text.Json;
using BinPacking.Web.Algorithms;
using BinPacking.Web.Models;

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
var algorithmName = args.Length > 1 ? args[1] : "baseline";
var targetedOnly = args.Any(arg => arg.Equals("--targeted", StringComparison.OrdinalIgnoreCase));
IPackingAlgorithm algorithm = algorithmName.Equals("hybrid", StringComparison.OrdinalIgnoreCase)
    ? new HybridPackingAlgorithm(PackingAlgorithmOptions.Balanced)
    : new ExtremePointPackingAlgorithm();
var results = new List<BenchmarkResult>();

// Warm JIT and static caches outside the measurements.
algorithm.Pack(BenchmarkCatalog.Box, BenchmarkCatalog.Create("Homogeneous", 6));

var scenarioNames = targetedOnly ? new[] { "EmsWasteTrap" } : BenchmarkCatalog.ScenarioNames;
var sizes = targetedOnly ? new[] { 30 } : BenchmarkCatalog.Sizes;
foreach (var scenario in scenarioNames)
foreach (var size in sizes)
{
    var items = BenchmarkCatalog.Create(scenario, size);
    var remaining = items.ToList();
    var attempts = new List<PackingAttempt>();
    while (remaining.Count > 0)
    {
        var attempt = algorithm.Pack(BenchmarkCatalog.Box, remaining);
        if (attempt.PackedItems.Count == 0) break;
        attempts.Add(attempt);
        var packedIds = attempt.PackedItems.Select(item => item.InstanceId).ToHashSet();
        remaining.RemoveAll(item => packedIds.Contains(item.InstanceId));
    }

    var packedVolume = attempts.Sum(attempt => attempt.PackedVolume);
    var totalBoxVolume = attempts.Count * BenchmarkCatalog.Box.Volume;
    results.Add(new BenchmarkResult(
        scenario,
        size,
        attempts.Sum(attempt => attempt.Diagnostics.CalculationTimeMs),
        packedVolume,
        totalBoxVolume == 0 ? 0 : packedVolume * 100d / totalBoxVolume,
        attempts.Count,
        totalBoxVolume,
        remaining.Count,
        attempts.Sum(attempt => attempt.Diagnostics.CandidateEvaluations),
        attempts.Sum(attempt => attempt.Diagnostics.ApproximateAllocatedBytes),
        attempts.FirstOrDefault()?.PackedVolume ?? 0,
        attempts.Count == 0 ? 0 : attempts[0].PackedVolume * 100d / BenchmarkCatalog.Box.Volume,
        attempts.FirstOrDefault()?.PackedItems.Count ?? 0));
    Console.WriteLine($"{scenario,-22} n={size,3} boxes={attempts.Count,2} util={results[^1].UtilizationPercent,6:F2}% ms={results[^1].CalculationTimeMs,9:F2}");
}

var orderedTimes = results.Select(result => result.CalculationTimeMs).Order().ToArray();
var summary = new BenchmarkReport(
    Environment.Version.ToString(),
    Environment.OSVersion.ToString(),
    DateTimeOffset.UtcNow,
    results,
    results.Average(result => result.UtilizationPercent),
    Percentile(orderedTimes, 0.50),
    Percentile(orderedTimes, 0.95),
    results.Sum(result => result.BoxCount),
    results.Sum(result => result.TotalBoxVolume),
    results.Sum(result => result.CandidateEvaluations),
    results.Sum(result => result.ApproximateAllocatedBytes));

var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
if (outputPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, json);
    Console.WriteLine($"Report: {outputPath}");
}
else
{
    Console.WriteLine(json);
}

static double Percentile(IReadOnlyList<double> sorted, double percentile)
{
    if (sorted.Count == 0) return 0;
    var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
}

internal sealed record BenchmarkResult(
    string Scenario,
    int ItemCount,
    double CalculationTimeMs,
    long PackedVolume,
    double UtilizationPercent,
    int BoxCount,
    long TotalBoxVolume,
    int UnpackedItemCount,
    long CandidateEvaluations,
    long ApproximateAllocatedBytes,
    long FirstBoxPackedVolume,
    double FirstBoxUtilizationPercent,
    int FirstBoxPackedItemCount);

internal sealed record BenchmarkReport(
    string Runtime,
    string OperatingSystem,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<BenchmarkResult> Results,
    double AverageUtilizationPercent,
    double P50CalculationTimeMs,
    double P95CalculationTimeMs,
    int TotalBoxCount,
    long TotalBoxVolume,
    long CandidateEvaluations,
    long ApproximateAllocatedBytes);

internal static class BenchmarkCatalog
{
    public static readonly int[] Sizes = [6, 10, 20, 50, 100, 200];
    public static readonly string[] ScenarioNames =
    [
        "Homogeneous", "WeaklyHeterogeneous", "StronglyHeterogeneous", "TallFlatMixed",
        "RotationSensitive", "HoleFilling", "GreedyTrap", "MultiBoxTrap"
    ];

    public static BoxType Box { get; } = new()
    {
        Id = StableGuid(9000), Name = "Benchmark 600x500x400",
        Length = 600, Width = 500, Height = 400, MaxWeightKg = 500, Cost = 10
    };

    public static IReadOnlyList<PackingItemUnit> Create(string scenario, int count)
    {
        var specs = scenario switch
        {
            "Homogeneous" => new[] { new Spec(120, 100, 80, true) },
            "WeaklyHeterogeneous" => new[] { new Spec(180, 120, 80, true), new Spec(150, 100, 100, true), new Spec(100, 100, 60, false), new Spec(80, 60, 50, true) },
            "StronglyHeterogeneous" => new[] { new Spec(220, 130, 90, true), new Spec(170, 160, 70, false), new Spec(140, 90, 130, true), new Spec(110, 80, 60, true), new Spec(95, 75, 150, true), new Spec(70, 65, 55, false), new Spec(190, 55, 45, true), new Spec(125, 115, 85, true) },
            "TallFlatMixed" => new[] { new Spec(90, 80, 300, false), new Spec(260, 180, 35, false), new Spec(80, 70, 240, true), new Spec(220, 160, 45, true) },
            "RotationSensitive" => new[] { new Spec(390, 130, 210, true), new Spec(310, 180, 120, true), new Spec(410, 90, 140, true), new Spec(280, 160, 110, true) },
            "HoleFilling" => new[] { new Spec(260, 220, 160, false), new Spec(250, 130, 160, false), new Spec(80, 90, 80, true), new Spec(70, 60, 60, true), new Spec(120, 50, 80, true) },
            "GreedyTrap" => new[] { new Spec(310, 250, 200, false), new Spec(290, 250, 200, false), new Spec(300, 250, 200, false), new Spec(150, 250, 200, false), new Spec(150, 250, 200, false) },
            "MultiBoxTrap" => new[] { new Spec(360, 260, 190, false), new Spec(240, 240, 190, false), new Spec(180, 130, 190, false), new Spec(120, 120, 190, false) },
            "EmsWasteTrap" => new[] { new Spec(170, 250, 210, true), new Spec(230, 250, 50, true), new Spec(120, 80, 80, true), new Spec(260, 110, 170, true), new Spec(240, 270, 90, true) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var units = new PackingItemUnit[count];
        for (var index = 0; index < count; index++)
        {
            var specIndex = scenario == "StronglyHeterogeneous"
                ? new Random(41_771 + index * 97).Next(specs.Length)
                : index % specs.Length;
            var spec = specs[specIndex];
            units[index] = new PackingItemUnit(
                StableGuid(index + 1), StableGuid(1000 + specIndex), $"{scenario}-{specIndex + 1}", index + 1,
                spec.Length, spec.Width, spec.Height, 1, spec.AllowRotation, "#60A5FA");
        }

        return units;
    }

    private static Guid StableGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        BitConverter.TryWriteBytes(bytes[8..], value * 486_187_739L);
        return new Guid(bytes);
    }

    private readonly record struct Spec(int Length, int Width, int Height, bool AllowRotation);
}
