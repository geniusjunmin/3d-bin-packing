namespace BinPacking.Web.Models;

public readonly record struct Point3(int X, int Y, int Z);

public readonly record struct OrientedSize(int Length, int Width, int Height, string Rotation)
{
    public long Volume => (long)Length * Width * Height;
}

public sealed record PackingItemUnit(
    Guid InstanceId,
    Guid ItemTypeId,
    string Name,
    int Sequence,
    int Length,
    int Width,
    int Height,
    double WeightKg,
    bool AllowRotation,
    string Color)
{
    public long Volume => (long)Length * Width * Height;
}

public sealed record PackedItem
{
    public required Guid InstanceId { get; init; }
    public required Guid ItemTypeId { get; init; }
    public required string Name { get; init; }
    public int Sequence { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public int Length { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int OriginalLength { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public required string Rotation { get; init; }
    public double WeightKg { get; init; }
    public required string Color { get; init; }
    public double SupportPercent { get; init; }
    public long Volume => (long)Length * Width * Height;
}

public sealed record PackingAttempt(
    BoxType Box,
    IReadOnlyList<PackedItem> PackedItems,
    IReadOnlyList<PackingItemUnit> UnpackedItems)
{
    public long PackedVolume => PackedItems.Sum(item => item.Volume);
    public double PackedWeightKg => PackedItems.Sum(item => item.WeightKg);
    public PackingDiagnostics Diagnostics { get; init; } = PackingDiagnostics.Empty;
}

public sealed record PackingDiagnostics
{
    public static PackingDiagnostics Empty { get; } = new();

    public string AlgorithmName { get; init; } = "Unknown";
    public string SearchMode { get; init; } = "Unknown";
    public double CalculationTimeMs { get; init; }
    public long CandidateEvaluations { get; init; }
    public long BeamNodesExpanded { get; init; }
    public long CacheHits { get; init; }
    public int ExtremePointCount { get; init; }
    public int EmsCount { get; init; }
    public long ApproximateAllocatedBytes { get; init; }
    public bool TimeBudgetReached { get; init; }
    public int BlockPlacements { get; init; }
    public int ItemsPackedAsBlocks { get; init; }
    public int LocalRepairAttempts { get; init; }
    public int LocalRepairSuccesses { get; init; }
}

public sealed record PackedBox
{
    public int Number { get; init; }
    public required BoxType Box { get; init; }
    public required IReadOnlyList<PackedItem> Items { get; init; }
    public double UtilizationPercent { get; init; }
    public double TotalWeightKg { get; init; }
}

public sealed record PackingSummary
{
    public int TotalItemCount { get; init; }
    public int TotalBoxCount { get; init; }
    public long TotalItemVolumeMm3 { get; init; }
    public long TotalBoxVolumeMm3 { get; init; }
    public double UtilizationPercent { get; init; }
    public decimal TotalCost { get; init; }
    public double CalculationTimeMs { get; init; }
    public required IReadOnlyDictionary<string, int> BoxesByType { get; init; }
}

public sealed record PackingResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public required PackingSummary Summary { get; init; }
    public required IReadOnlyList<PackedBox> Boxes { get; init; }
    public required IReadOnlyList<string> UnpackedItems { get; init; }
}
