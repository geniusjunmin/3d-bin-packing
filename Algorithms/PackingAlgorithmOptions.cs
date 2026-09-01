namespace BinPacking.Web.Algorithms;

public enum PackingSearchMode
{
    Fast,
    Balanced,
    Quality
}

public sealed record PackingAlgorithmOptions
{
    public PackingSearchMode SearchMode { get; init; } = PackingSearchMode.Balanced;
    public double EarlyStageThreshold { get; init; } = 0.65;
    public double WasteWeight { get; init; } = 7.0;
    public double ContactWeight { get; init; } = 1.5;
    public double EnvelopeWeight { get; init; } = 2.5;
    public double HeightWeight { get; init; } = 1.0;
    public double FragmentationWeight { get; init; } = 2.0;
    public double FutureFitWeight { get; init; } = 4.0;
    public double SupportWeight { get; init; } = 0.25;
    public double MinimumSupportRatio { get; init; } = 0.90;
    public double MinimumQuadrantSupportRatio { get; init; } = 0.70;
    public int DifficultyTopK { get; init; } = 10;
    public int MaxExtremePoints { get; init; } = 160;
    public int MaxEmptySpaces { get; init; } = 128;
    public int BeamWidth { get; init; } = 4;
    public int BranchFactor { get; init; } = 4;
    public int LookaheadDepth { get; init; } = 2;
    public int MemoizationCapacity { get; init; } = 4_096;
    public int TimeBudgetMs { get; init; } = 250;
    public bool EnableBeamSearch { get; init; } = true;
    public bool EnableLegacyFallback { get; init; } = true;

    public static PackingAlgorithmOptions Fast { get; } = new()
    {
        SearchMode = PackingSearchMode.Fast,
        DifficultyTopK = 8,
        MaxExtremePoints = 96,
        MaxEmptySpaces = 72,
        BeamWidth = 2,
        BranchFactor = 2,
        LookaheadDepth = 1,
        TimeBudgetMs = 80,
        EnableBeamSearch = false,
        EnableLegacyFallback = false
    };

    public static PackingAlgorithmOptions Balanced { get; } = new();

    public static PackingAlgorithmOptions Quality { get; } = new()
    {
        SearchMode = PackingSearchMode.Quality,
        DifficultyTopK = 16,
        MaxExtremePoints = 256,
        MaxEmptySpaces = 192,
        BeamWidth = 8,
        BranchFactor = 5,
        LookaheadDepth = 4,
        MemoizationCapacity = 12_000,
        TimeBudgetMs = 1_200
    };
}
