using System.Diagnostics;
using BinPacking.Web.Models;
using Microsoft.Extensions.Options;

namespace BinPacking.Web.Algorithms;

/// <summary>
/// Deterministic hybrid of extreme points and empty maximal spaces. Greedy
/// placement is always available; bounded lookahead is used only near difficult
/// or fragmented states and is constrained by width, depth and a time budget.
/// </summary>
public sealed class HybridPackingAlgorithm : IPackingAlgorithm
{
    private readonly PackingAlgorithmOptions _options;
    private readonly EmptySpaceManager _emptySpaces = new();
    private readonly ExtremePointManager _extremePoints = new();

    public HybridPackingAlgorithm() : this(PackingAlgorithmOptions.Balanced) { }

    public HybridPackingAlgorithm(IOptions<PackingAlgorithmOptions> options) : this(options.Value) { }

    public HybridPackingAlgorithm(PackingAlgorithmOptions options)
    {
        _options = options;
    }

    public PackingAttempt Pack(BoxType box, IReadOnlyList<PackingItemUnit> items)
    {
        PackingAttempt? compatibilityCandidate = null;
        var distinctTypes = items.Select(ItemSignature.From).Distinct().Count();
        if (_options.EnableLegacyFallback && distinctTypes > 1)
        {
            compatibilityCandidate = new ExtremePointPackingAlgorithm().Pack(box, items);
            var compatibilityFill = compatibilityCandidate.PackedVolume / (double)box.Volume;
            var requestedVolumeRatio = items.Sum(item => item.Volume) / (double)box.Volume;
            var smallDifficultState = items.Count <= 40 && distinctTypes >= 5 &&
                                      requestedVolumeRatio is >= 1.0 and <= 1.25 && compatibilityFill < 0.85;
            var largeFragmentedState = items.Count > 150 && compatibilityFill < 0.70;
            // The measured legacy portfolio remains both faster and at least as
            // effective on ordinary heterogeneous orders. Run EMS lookahead only
            // for near-full small traps or large low-fill states.
            if (compatibilityCandidate.UnpackedItems.Count == 0 || !smallDifficultState && !largeFragmentedState)
            {
                return compatibilityCandidate with
                {
                    Diagnostics = compatibilityCandidate.Diagnostics with
                    {
                        AlgorithmName = $"{nameof(HybridPackingAlgorithm)}+FastCompatibilityPath",
                        SearchMode = _options.SearchMode.ToString()
                    }
                };
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var metrics = new RunMetrics();
        var orientationCache = BuildOrientationCache(items);
        var orderedItems = items.OrderBy(ItemKey).ToList();
        var state = new PackingState(
            [], orderedItems, BuildSkuStates(orderedItems, orientationCache), _extremePoints.Create(), _emptySpaces.Create(box),
            0, 0, 0, 0, 0);
        var memo = new Dictionary<StateKey, long>();

        while (state.Remaining.Count > 0)
        {
            var candidates = FindCandidates(box, state, orientationCache, metrics, _options.BranchFactor * 2);
            if (candidates.Count == 0) break;

            var selected = candidates[0];
            if (ShouldSearch(state, box, candidates, stopwatch))
            {
                selected = ChooseWithLookahead(box, state, candidates, orientationCache, metrics, memo, stopwatch);
            }

            state = Place(box, state, selected, metrics, true);
        }

        state = TryLocalRepair(box, items, state, orientationCache, metrics);

        stopwatch.Stop();
        var hybrid = new PackingAttempt(box, state.Packed, state.Remaining)
        {
            Diagnostics = new PackingDiagnostics
            {
                AlgorithmName = nameof(HybridPackingAlgorithm),
                SearchMode = _options.SearchMode.ToString(),
                CalculationTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                CandidateEvaluations = metrics.CandidateEvaluations,
                BeamNodesExpanded = metrics.BeamNodesExpanded,
                CacheHits = metrics.CacheHits,
                ExtremePointCount = metrics.PeakExtremePoints,
                EmsCount = metrics.PeakEmptySpaces,
                ApproximateAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
                TimeBudgetReached = metrics.TimeBudgetReached,
                BlockPlacements = metrics.BlockPlacements,
                ItemsPackedAsBlocks = metrics.ItemsPackedAsBlocks,
                LocalRepairAttempts = metrics.LocalRepairAttempts,
                LocalRepairSuccesses = metrics.LocalRepairSuccesses
            }
        };
        if (!_options.EnableLegacyFallback || hybrid.UnpackedItems.Count == 0 && compatibilityCandidate is null) return hybrid;

        var baseline = compatibilityCandidate ?? new ExtremePointPackingAlgorithm().Pack(box, items);
        if (baseline.PackedVolume < hybrid.PackedVolume ||
            baseline.PackedVolume == hybrid.PackedVolume && baseline.PackedItems.Count <= hybrid.PackedItems.Count)
            return hybrid;
        return baseline with
        {
            Diagnostics = baseline.Diagnostics with
            {
                AlgorithmName = $"{nameof(HybridPackingAlgorithm)}+CompatibilityFallback",
                SearchMode = _options.SearchMode.ToString(),
                CalculationTimeMs = hybrid.Diagnostics.CalculationTimeMs + baseline.Diagnostics.CalculationTimeMs,
                CandidateEvaluations = hybrid.Diagnostics.CandidateEvaluations + baseline.Diagnostics.CandidateEvaluations,
                ApproximateAllocatedBytes = hybrid.Diagnostics.ApproximateAllocatedBytes + baseline.Diagnostics.ApproximateAllocatedBytes,
                BeamNodesExpanded = hybrid.Diagnostics.BeamNodesExpanded,
                CacheHits = hybrid.Diagnostics.CacheHits,
                EmsCount = hybrid.Diagnostics.EmsCount,
                TimeBudgetReached = hybrid.Diagnostics.TimeBudgetReached,
                BlockPlacements = hybrid.Diagnostics.BlockPlacements,
                ItemsPackedAsBlocks = hybrid.Diagnostics.ItemsPackedAsBlocks,
                LocalRepairAttempts = hybrid.Diagnostics.LocalRepairAttempts,
                LocalRepairSuccesses = hybrid.Diagnostics.LocalRepairSuccesses
            }
        };
    }

    private Candidate ChooseWithLookahead(
        BoxType box,
        PackingState initial,
        IReadOnlyList<Candidate> initialCandidates,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics,
        Dictionary<StateKey, long> memo,
        Stopwatch stopwatch)
    {
        var depthLimit = initial.Remaining.Count <= 6 && _options.SearchMode == PackingSearchMode.Quality
            ? Math.Min(6, initial.Remaining.Count)
            : _options.LookaheadDepth;
        var beam = initialCandidates.Take(_options.BranchFactor)
            .Select(candidate => new BeamNode(Place(box, initial, candidate, metrics), candidate, candidate.Score.Value))
            .ToList();

        for (var depth = 1; depth < depthLimit && beam.Count > 0; depth++)
        {
            if (stopwatch.ElapsedMilliseconds >= _options.TimeBudgetMs)
            {
                metrics.TimeBudgetReached = true;
                break;
            }

            var expanded = new List<BeamNode>(_options.BeamWidth * _options.BranchFactor);
            foreach (var node in beam)
            {
                var candidates = FindCandidates(box, node.State, orientationCache, metrics, _options.BranchFactor);
                foreach (var candidate in candidates)
                {
                    if (stopwatch.ElapsedMilliseconds >= _options.TimeBudgetMs)
                    {
                        metrics.TimeBudgetReached = true;
                        break;
                    }

                    var next = Place(box, node.State, candidate, metrics);
                    metrics.BeamNodesExpanded++;
                    var hash = BuildStateHash(next);
                    if (memo.TryGetValue(hash, out var knownVolume) && knownVolume >= next.PackedVolume)
                    {
                        metrics.CacheHits++;
                        continue;
                    }
                    if (memo.Count < _options.MemoizationCapacity) memo[hash] = next.PackedVolume;
                    expanded.Add(new BeamNode(next, node.First, node.AccumulatedScore + candidate.Score.Value));
                }
            }

            beam = expanded
                .OrderByDescending(node => node.State.PackedVolume)
                .ThenByDescending(node => FutureFitVolume(node.State))
                .ThenBy(node => node.AccumulatedScore)
                .ThenBy(node => node.First.Item.ItemTypeId)
                .ThenBy(node => node.First.Item.Sequence)
                .Take(_options.BeamWidth)
                .ToList();
        }

        return beam
            .OrderByDescending(node => node.State.PackedVolume)
            .ThenByDescending(node => FutureFitVolume(node.State))
            .ThenBy(node => node.AccumulatedScore)
            .ThenBy(node => node.First.Score)
            .Select(node => node.First)
            .FirstOrDefault() ?? initialCandidates[0];
    }

    private PackingState TryLocalRepair(
        BoxType box,
        IReadOnlyList<PackingItemUnit> allItems,
        PackingState incumbent,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics)
    {
        if (!_options.EnableLocalRepair || incumbent.Remaining.Count is < 1 or > 4 || incumbent.Packed.Count < 3) return incumbent;

        var stopwatch = Stopwatch.StartNew();
        var best = incumbent;
        for (var attempt = 0; attempt < _options.RepairAttempts; attempt++)
        {
            if (stopwatch.ElapsedMilliseconds >= _options.RepairTimeBudgetMs) break;
            var removeCount = Math.Min(incumbent.Packed.Count, Math.Max(3, _options.RepairRemoveCount - 1 + attempt));
            var retained = incumbent.Packed.Take(incumbent.Packed.Count - removeCount).ToList();
            var candidateState = RebuildState(box, allItems, retained, orientationCache, metrics);
            metrics.LocalRepairAttempts++;
            var chooseAlternative = attempt;
            while (candidateState.Remaining.Count > 0 && stopwatch.ElapsedMilliseconds < _options.RepairTimeBudgetMs)
            {
                var candidates = FindCandidates(box, candidateState, orientationCache, metrics, _options.BranchFactor * 2);
                if (candidates.Count == 0) break;
                var selectedIndex = chooseAlternative == 0 ? 0 : Math.Min(chooseAlternative, candidates.Count - 1);
                candidateState = Place(box, candidateState, candidates[selectedIndex], metrics);
                chooseAlternative = 0;
            }

            if (IsBetterPacking(candidateState, best)) best = candidateState;
            if (best.Remaining.Count == 0) break;
        }

        if (!ReferenceEquals(best, incumbent)) metrics.LocalRepairSuccesses++;
        return best;
    }

    private PackingState RebuildState(
        BoxType box,
        IReadOnlyList<PackingItemUnit> allItems,
        IReadOnlyList<PackedItem> retained,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics)
    {
        var remaining = allItems.OrderBy(ItemKey).ToList();
        var packed = new List<PackedItem>(retained.Count);
        var spaces = _emptySpaces.Create(box);
        var points = _extremePoints.Create();
        var currentWeight = 0d;
        var maxX = 0; var maxY = 0; var maxZ = 0; long packedVolume = 0;
        foreach (var item in retained)
        {
            remaining.RemoveAll(unit => unit.InstanceId == item.InstanceId);
            var skusAfter = BuildSkuStates(remaining, orientationCache);
            var size = new OrientedSize(item.Length, item.Width, item.Height, item.Rotation);
            spaces = _emptySpaces.Place(spaces, new Point3(item.X, item.Y, item.Z), size, GetRemainingOrientationSets(skusAfter), _options.MaxEmptySpaces);
            packed.Add(item);
            points = _extremePoints.Place(points, item, box, packed, spaces, _options.MaxExtremePoints);
            currentWeight += item.WeightKg; packedVolume += item.Volume;
            maxX = Math.Max(maxX, item.X + item.Length); maxY = Math.Max(maxY, item.Y + item.Width); maxZ = Math.Max(maxZ, item.Z + item.Height);
        }
        metrics.PeakExtremePoints = Math.Max(metrics.PeakExtremePoints, points.Count);
        metrics.PeakEmptySpaces = Math.Max(metrics.PeakEmptySpaces, spaces.Count);
        return new PackingState(packed, remaining, BuildSkuStates(remaining, orientationCache), points, spaces, currentWeight, maxX, maxY, maxZ, packedVolume);
    }

    private static bool IsBetterPacking(PackingState candidate, PackingState incumbent) =>
        candidate.Packed.Count > incumbent.Packed.Count ||
        candidate.Packed.Count == incumbent.Packed.Count && candidate.PackedVolume > incumbent.PackedVolume;

    private List<Candidate> FindCandidates(
        BoxType box,
        PackingState state,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics,
        int take)
    {
        var representativeItems = state.RemainingSkus
            .Select(sku => new { Sku = sku, Difficulty = Difficulty(sku, box, state.EmptySpaces) })
            .OrderByDescending(entry => entry.Difficulty)
            .ThenByDescending(entry => entry.Sku.Representative.Volume)
            .ThenBy(entry => ItemKey(entry.Sku.Representative))
            .Take(_options.DifficultyTopK)
            .Select(entry => entry.Sku)
            .ToArray();
        var candidates = new List<Candidate>(take * 4);

        foreach (var sku in representativeItems)
        {
            var item = sku.Representative;
            foreach (var shape in BuildPlacementShapes(sku))
            {
                if (box.MaxWeightKg is { } capacity && state.CurrentWeight + item.WeightKg * shape.ItemCount > capacity + 1e-9) continue;
                var remainingAfter = GetRemainingOrientationSetsAfter(state.RemainingSkus, sku.Signature, shape.ItemCount);
                var points = CandidatePoints(state, shape.Size);
                foreach (var point in points)
                {
                    metrics.CandidateEvaluations++;
                    if (point.X + shape.Size.Length > box.Length || point.Y + shape.Size.Width > box.Width || point.Z + shape.Size.Height > box.Height) continue;
                    if (!state.EmptySpaces.Any(space => space.Contains(point, shape.Size))) continue;
                    if (OverlapsAny(point, shape.Size, state.Packed)) continue;
                    var stability = MeasureShapeStability(point, shape, state.Packed);
                    if (!IsStable(stability)) continue;

                    var nextSpaces = _emptySpaces.Place(state.EmptySpaces, point, shape.Size, remainingAfter, _options.MaxEmptySpaces);
                    var score = Score(box, state, sku, shape, point, stability, nextSpaces, remainingAfter);
                    candidates.Add(new Candidate(item, sku.Signature, point, shape, stability, nextSpaces, score));
                }
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Shape.Volume)
            .ThenBy(candidate => ItemKey(candidate.Item))
            .Take(take)
            .ToList();
    }

    private IReadOnlyList<PlacementShape> BuildPlacementShapes(SkuState sku)
    {
        var result = new List<PlacementShape>(sku.Orientations.Count + _options.MaxBlockCandidatesPerSku);
        foreach (var orientation in sku.Orientations)
            result.Add(new PlacementShape(orientation, 1, 1, 1));
        if (!_options.EnableBlockPacking || sku.Count < _options.MinimumRepeatedCount) return result;

        ReadOnlySpan<(int X, int Y, int Z)> patterns =
        [
            (1, 2, 1), (2, 2, 1), (2, 3, 1), (3, 3, 1),
            (2, 2, 2), (3, 2, 2), (3, 3, 2), (4, 3, 2)
        ];
        var blocks = new List<PlacementShape>(_options.MaxBlockCandidatesPerSku * sku.Orientations.Count);
        var signatures = new HashSet<(int Length, int Width, int Height, int Count)>();
        foreach (var orientation in sku.Orientations)
        foreach (var pattern in patterns)
        {
            var count = pattern.X * pattern.Y * pattern.Z;
            if (count > sku.Count || count > _options.MaxBlockItemCount) continue;
            var block = new PlacementShape(orientation, pattern.X, pattern.Y, pattern.Z);
            if (signatures.Add((block.Size.Length, block.Size.Width, block.Size.Height, count))) blocks.Add(block);
        }
        result.AddRange(blocks.OrderByDescending(block => block.ItemCount)
            .ThenBy(block => block.Size.Height).ThenBy(block => block.Size.Width).ThenBy(block => block.Size.Length)
            .Take(_options.MaxBlockCandidatesPerSku));
        return result;
    }

    private IReadOnlyList<Point3> CandidatePoints(PackingState state, OrientedSize size)
    {
        var points = new HashSet<Point3>(state.ExtremePoints);
        foreach (var space in state.EmptySpaces)
        {
            if (size.Length > space.Length || size.Width > space.Width || size.Height > space.Height) continue;
            points.Add(new Point3(space.X, space.Y, space.Z));
            points.Add(new Point3(space.Right - size.Length, space.Y, space.Z));
            points.Add(new Point3(space.X, space.Back - size.Width, space.Z));
            points.Add(new Point3(space.Right - size.Length, space.Back - size.Width, space.Z));
        }

        return points.OrderBy(point => point.Z).ThenBy(point => point.Y).ThenBy(point => point.X)
            .Take(_options.MaxExtremePoints).ToArray();
    }

    private PackingState Place(
        BoxType box,
        PackingState state,
        Candidate candidate,
        RunMetrics metrics,
        bool recordPlacement = false)
    {
        var selectedItems = state.Remaining.Where(item => ItemSignature.From(item) == candidate.Signature)
            .Take(candidate.Shape.ItemCount).ToArray();
        var packed = new List<PackedItem>(state.Packed.Count + selectedItems.Length);
        packed.AddRange(state.Packed);
        var itemIndex = 0;
        for (var z = 0; z < candidate.Shape.CountZ; z++)
        for (var y = 0; y < candidate.Shape.CountY; y++)
        for (var x = 0; x < candidate.Shape.CountX; x++)
        {
            var item = selectedItems[itemIndex++];
            var itemPoint = new Point3(
                candidate.Point.X + x * candidate.Shape.UnitSize.Length,
                candidate.Point.Y + y * candidate.Shape.UnitSize.Width,
                candidate.Point.Z + z * candidate.Shape.UnitSize.Height);
            var unitStability = z == 0 ? MeasureStability(itemPoint, candidate.Shape.UnitSize, state.Packed) : new StabilityMetrics(1, 1, true);
            packed.Add(CreatePackedItem(item, itemPoint, candidate.Shape.UnitSize, unitStability));
        }
        var removedIds = selectedItems.Select(item => item.InstanceId).ToHashSet();
        var remaining = state.Remaining.Where(item => !removedIds.Contains(item.InstanceId)).ToList();
        var remainingSkus = RemoveFromSkuState(state.RemainingSkus, candidate.Signature, candidate.Shape.ItemCount, remaining);
        var virtualBlock = CreatePackedItem(candidate.Item, candidate.Point, candidate.Shape.Size, candidate.Stability) with
        {
            WeightKg = candidate.Item.WeightKg * candidate.Shape.ItemCount
        };
        var points = _extremePoints.Place(state.ExtremePoints, virtualBlock, box, packed, candidate.NextSpaces, _options.MaxExtremePoints);
        metrics.PeakExtremePoints = Math.Max(metrics.PeakExtremePoints, points.Count);
        metrics.PeakEmptySpaces = Math.Max(metrics.PeakEmptySpaces, candidate.NextSpaces.Count);
        if (recordPlacement && candidate.Shape.ItemCount > 1)
        {
            metrics.BlockPlacements++;
            metrics.ItemsPackedAsBlocks += candidate.Shape.ItemCount;
        }
        return new PackingState(
            packed, remaining, remainingSkus, points, candidate.NextSpaces,
            state.CurrentWeight + candidate.Item.WeightKg * candidate.Shape.ItemCount,
            Math.Max(state.MaxX, candidate.Point.X + candidate.Shape.Size.Length),
            Math.Max(state.MaxY, candidate.Point.Y + candidate.Shape.Size.Width),
            Math.Max(state.MaxZ, candidate.Point.Z + candidate.Shape.Size.Height),
            state.PackedVolume + candidate.Shape.Volume);
    }

    private static PackedItem CreatePackedItem(PackingItemUnit item, Point3 point, OrientedSize size, StabilityMetrics stability) => new()
    {
        InstanceId = item.InstanceId, ItemTypeId = item.ItemTypeId, Name = item.Name, Sequence = item.Sequence,
        X = point.X, Y = point.Y, Z = point.Z, Length = size.Length, Width = size.Width, Height = size.Height,
        OriginalLength = item.Length, OriginalWidth = item.Width, OriginalHeight = item.Height,
        Rotation = size.Rotation, WeightKg = item.WeightKg, Color = item.Color,
        SupportPercent = Math.Round(stability.SupportRatio * 100, 2)
    };

    private CandidateScore Score(
        BoxType box,
        PackingState state,
        SkuState sku,
        PlacementShape shape,
        Point3 point,
        StabilityMetrics stability,
        IReadOnlyList<EmptySpace> nextSpaces,
        IReadOnlyList<IReadOnlyList<OrientedSize>> remainingOrientations)
    {
        var item = sku.Representative;
        var size = shape.Size;
        var boxVolume = (double)box.Volume;
        var fillRatio = (state.PackedVolume + shape.Volume) / boxVolume;
        long wastedVolume = 0;
        var containing = state.EmptySpaces.Where(space => space.Contains(point, size)).OrderBy(space => space.Volume).First();
        foreach (var residual in EmptySpaceManager.PartitionResidual(containing, point, size))
            if (!EmptySpaceManager.CanFitAny(residual, remainingOrientations)) wastedVolume += residual.Volume;

        long futureFitVolume = 0;
        foreach (var remainingSku in state.RemainingSkus)
        {
            var count = remainingSku.Count - (remainingSku.Signature == sku.Signature ? shape.ItemCount : 0);
            if (count <= 0) continue;
            if (nextSpaces.Any(space => remainingSku.Orientations.Any(oriented => oriented.Length <= space.Length && oriented.Width <= space.Width && oriented.Height <= space.Height)))
                futureFitVolume += remainingSku.Representative.Volume * count;
        }

        var remainingVolume = Math.Max(1d, state.RemainingSkus.Sum(sku => (double)sku.Representative.Volume * sku.Count) - shape.Volume);
        var envelope = (long)Math.Max(state.MaxX, point.X + size.Length) *
                       Math.Max(state.MaxY, point.Y + size.Width) *
                       Math.Max(state.MaxZ, point.Z + size.Height);
        var contact = ContactArea(point, size, state.Packed);
        var fragmentation = nextSpaces.Count / (double)_options.MaxEmptySpaces;
        var late = fillRatio >= _options.EarlyStageThreshold;
        var wasteWeight = _options.WasteWeight * (late ? 1.6 : 0.55);
        var futureWeight = _options.FutureFitWeight * (late ? 1.5 : 0.6);
        var envelopeWeight = _options.EnvelopeWeight * (late ? 0.65 : 1.5);
        var contactWeight = _options.ContactWeight * (late ? 0.8 : 1.35);
        var value =
            wasteWeight * wastedVolume / boxVolume +
            _options.FragmentationWeight * fragmentation +
            envelopeWeight * envelope / boxVolume +
            _options.HeightWeight * (point.Z + size.Height) / box.Height -
            futureWeight * futureFitVolume / remainingVolume -
            contactWeight * contact / Math.Max(1d, 2d * (size.Length * (double)size.Width + size.Length * (double)size.Height + size.Width * (double)size.Height)) -
            _options.SupportWeight * stability.SupportRatio -
            _options.BlockRewardWeight * (shape.ItemCount - 1d) / _options.MaxBlockItemCount;
        return new CandidateScore(value, wastedVolume, -futureFitVolume, nextSpaces.Count, point.Z + size.Height, point.Z, point.Y, point.X, size.Rotation);
    }

    private bool ShouldSearch(PackingState state, BoxType box, IReadOnlyList<Candidate> candidates, Stopwatch stopwatch)
    {
        if (!_options.EnableBeamSearch || _options.LookaheadDepth <= 1 || stopwatch.ElapsedMilliseconds >= _options.TimeBudgetMs) return false;
        if (state.RemainingSkus.Count == 1 && _options.EnableBlockPacking) return false;
        var fill = state.PackedVolume / (double)box.Volume;
        var close = candidates.Count > 1 && Math.Abs(candidates[1].Score.Value - candidates[0].Score.Value) < 0.08;
        return fill >= 0.50 || state.Remaining.Count <= 12 || state.EmptySpaces.Count >= 18 || close;
    }

    private double Difficulty(
        SkuState sku,
        BoxType box,
        IReadOnlyList<EmptySpace> spaces)
    {
        var item = sku.Representative;
        var orientations = sku.Orientations;
        var fitCount = 0;
        foreach (var space in spaces)
        foreach (var size in orientations)
            if (size.Length <= space.Length && size.Width <= space.Width && size.Height <= space.Height) fitCount++;
        var longest = Math.Max(item.Length, Math.Max(item.Width, item.Height));
        var boxLongest = Math.Max(box.Length, Math.Max(box.Width, box.Height));
        return 8d / (1 + fitCount) + (item.AllowRotation ? 0 : 2.5) + 1.5d / orientations.Count +
               5d * item.Volume / box.Volume + 2d * longest / boxLongest;
    }

    private bool IsStable(StabilityMetrics stability) =>
        stability.CenterSupported && stability.SupportRatio + 1e-9 >= _options.MinimumSupportRatio &&
        stability.MinimumQuadrantRatio + 1e-9 >= _options.MinimumQuadrantSupportRatio;

    private StabilityMetrics MeasureShapeStability(Point3 point, PlacementShape shape, IReadOnlyList<PackedItem> packed)
    {
        if (point.Z == 0) return new StabilityMetrics(1, 1, true);
        var minimumSupport = 1d;
        var minimumQuadrant = 1d;
        for (var y = 0; y < shape.CountY; y++)
        for (var x = 0; x < shape.CountX; x++)
        {
            var unitPoint = new Point3(point.X + x * shape.UnitSize.Length, point.Y + y * shape.UnitSize.Width, point.Z);
            var stability = MeasureStability(unitPoint, shape.UnitSize, packed);
            if (!IsStable(stability)) return stability;
            minimumSupport = Math.Min(minimumSupport, stability.SupportRatio);
            minimumQuadrant = Math.Min(minimumQuadrant, stability.MinimumQuadrantRatio);
        }
        return new StabilityMetrics(minimumSupport, minimumQuadrant, true);
    }

    private static StabilityMetrics MeasureStability(Point3 point, OrientedSize size, IReadOnlyList<PackedItem> packed)
    {
        if (point.Z == 0) return new StabilityMetrics(1, 1, true);
        var footprint = new Rectangle2(point.X, point.Y, point.X + size.Length, point.Y + size.Width);
        var surfaces = new List<Rectangle2>();
        foreach (var item in packed)
        {
            if (item.Z + item.Height != point.Z) continue;
            var overlap = Intersect(footprint, new Rectangle2(item.X, item.Y, item.X + item.Length, item.Y + item.Width));
            if (overlap.Area > 0) surfaces.Add(overlap);
        }
        if (surfaces.Count == 0) return new StabilityMetrics(0, 0, false);
        if (surfaces.Count == 1 && surfaces[0].Area >= footprint.Area - 1e-9) return new StabilityMetrics(1, 1, true);

        var supportRatio = CoveredArea(footprint, surfaces) / footprint.Area;
        var centerX = (footprint.Left + footprint.Right) / 2d;
        var centerY = (footprint.Bottom + footprint.Top) / 2d;
        var center = surfaces.Any(surface => centerX >= surface.Left && centerX <= surface.Right && centerY >= surface.Bottom && centerY <= surface.Top);
        var quadrants = new[]
        {
            new Rectangle2(footprint.Left, footprint.Bottom, centerX, centerY), new Rectangle2(centerX, footprint.Bottom, footprint.Right, centerY),
            new Rectangle2(footprint.Left, centerY, centerX, footprint.Top), new Rectangle2(centerX, centerY, footprint.Right, footprint.Top)
        };
        var minimumQuadrant = quadrants.Min(quadrant => CoveredArea(quadrant, surfaces) / quadrant.Area);
        return new StabilityMetrics(supportRatio, minimumQuadrant, center);
    }

    private static double CoveredArea(Rectangle2 target, IReadOnlyList<Rectangle2> surfaces)
    {
        var xs = new SortedSet<double> { target.Left, target.Right };
        var ys = new SortedSet<double> { target.Bottom, target.Top };
        foreach (var surface in surfaces)
        {
            var clipped = Intersect(target, surface);
            if (clipped.Area <= 0) continue;
            xs.Add(clipped.Left); xs.Add(clipped.Right); ys.Add(clipped.Bottom); ys.Add(clipped.Top);
        }
        var x = xs.ToArray(); var y = ys.ToArray(); var area = 0d;
        for (var xi = 0; xi < x.Length - 1; xi++)
        for (var yi = 0; yi < y.Length - 1; yi++)
        {
            var cx = (x[xi] + x[xi + 1]) / 2; var cy = (y[yi] + y[yi + 1]) / 2;
            if (surfaces.Any(surface => cx > surface.Left && cx < surface.Right && cy > surface.Bottom && cy < surface.Top))
                area += (x[xi + 1] - x[xi]) * (y[yi + 1] - y[yi]);
        }
        return area;
    }

    private static bool OverlapsAny(Point3 point, OrientedSize size, IReadOnlyList<PackedItem> packed)
    {
        for (var index = 0; index < packed.Count; index++)
        {
            var other = packed[index];
            if (point.X < other.X + other.Length && point.X + size.Length > other.X &&
                point.Y < other.Y + other.Width && point.Y + size.Width > other.Y &&
                point.Z < other.Z + other.Height && point.Z + size.Height > other.Z) return true;
        }
        return false;
    }

    private static long ContactArea(Point3 point, OrientedSize size, IReadOnlyList<PackedItem> packed)
    {
        long area = 0;
        if (point.X == 0) area += (long)size.Width * size.Height;
        if (point.Y == 0) area += (long)size.Length * size.Height;
        if (point.Z == 0) area += (long)size.Length * size.Width;
        foreach (var other in packed)
        {
            if (point.X == other.X + other.Length || point.X + size.Length == other.X) area += Overlap(point.Y, size.Width, other.Y, other.Width) * Overlap(point.Z, size.Height, other.Z, other.Height);
            if (point.Y == other.Y + other.Width || point.Y + size.Width == other.Y) area += Overlap(point.X, size.Length, other.X, other.Length) * Overlap(point.Z, size.Height, other.Z, other.Height);
            if (point.Z == other.Z + other.Height || point.Z + size.Height == other.Z) area += Overlap(point.X, size.Length, other.X, other.Length) * Overlap(point.Y, size.Width, other.Y, other.Width);
        }
        return area;
    }

    private static long FutureFitVolume(PackingState state)
    {
        long volume = 0;
        foreach (var sku in state.RemainingSkus)
        {
            if (state.EmptySpaces.Any(space => sku.Orientations.Any(size => size.Length <= space.Length && size.Width <= space.Width && size.Height <= space.Height)))
                volume += sku.Representative.Volume * sku.Count;
        }
        return volume;
    }

    private static Dictionary<ItemSignature, IReadOnlyList<OrientedSize>> BuildOrientationCache(IReadOnlyList<PackingItemUnit> items)
    {
        var result = new Dictionary<ItemSignature, IReadOnlyList<OrientedSize>>();
        foreach (var item in items)
        {
            var key = ItemSignature.From(item);
            if (result.ContainsKey(key)) continue;
            var candidates = item.AllowRotation
                ? new[] { new OrientedSize(item.Length,item.Width,item.Height,"L-W-H"), new(item.Length,item.Height,item.Width,"L-H-W"), new(item.Width,item.Length,item.Height,"W-L-H"), new(item.Width,item.Height,item.Length,"W-H-L"), new(item.Height,item.Length,item.Width,"H-L-W"), new(item.Height,item.Width,item.Length,"H-W-L") }
                : [new OrientedSize(item.Length, item.Width, item.Height, "L-W-H")];
            result[key] = candidates.DistinctBy(size => (size.Length, size.Width, size.Height)).ToArray();
        }
        return result;
    }

    private static List<SkuState> BuildSkuStates(
        IReadOnlyList<PackingItemUnit> remaining,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> cache) =>
        remaining.GroupBy(ItemSignature.From)
            .OrderBy(group => group.Key)
            .Select(group => new SkuState(group.Key, group.First(), group.Count(), cache[group.Key]))
            .ToList();

    private static List<IReadOnlyList<OrientedSize>> GetRemainingOrientationSets(IReadOnlyList<SkuState> skus) =>
        skus.Select(sku => sku.Orientations).ToList();

    private static List<IReadOnlyList<OrientedSize>> GetRemainingOrientationSetsAfter(
        IReadOnlyList<SkuState> skus,
        ItemSignature removed,
        int removeCount)
    {
        var result = new List<IReadOnlyList<OrientedSize>>(skus.Count);
        foreach (var sku in skus)
            if (sku.Signature != removed || sku.Count > removeCount) result.Add(sku.Orientations);
        return result;
    }

    private static List<SkuState> RemoveFromSkuState(
        IReadOnlyList<SkuState> skus,
        ItemSignature removed,
        int removeCount,
        IReadOnlyList<PackingItemUnit> remaining)
    {
        var result = new List<SkuState>(skus.Count);
        foreach (var sku in skus)
        {
            if (sku.Signature != removed) { result.Add(sku); continue; }
            var count = sku.Count - removeCount;
            if (count <= 0) continue;
            var representative = remaining.First(item => ItemSignature.From(item) == removed);
            result.Add(sku with { Representative = representative, Count = count });
        }
        return result;
    }

    private static StateKey BuildStateHash(PackingState state)
    {
        var high = 14_695_981_039_346_656_037UL;
        var low = 10_995_116_282_11UL;
        foreach (var sku in state.RemainingSkus)
        {
            Mix(ref high, ref low, (ulong)(uint)sku.Signature.Length);
            Mix(ref high, ref low, (ulong)(uint)sku.Signature.Width);
            Mix(ref high, ref low, (ulong)(uint)sku.Signature.Height);
            Mix(ref high, ref low, sku.Signature.AllowRotation ? 1UL : 0UL);
            Mix(ref high, ref low, (ulong)sku.Signature.WeightBits);
            Mix(ref high, ref low, (ulong)(uint)sku.Count);
        }
        Mix(ref high, ref low, (ulong)state.PackedVolume);
        Mix(ref high, ref low, (ulong)Math.Round(state.CurrentWeight * 1_000_000));
        foreach (var space in state.EmptySpaces)
        {
            Mix(ref high, ref low, (ulong)(uint)space.X); Mix(ref high, ref low, (ulong)(uint)space.Y); Mix(ref high, ref low, (ulong)(uint)space.Z);
            Mix(ref high, ref low, (ulong)(uint)space.Length); Mix(ref high, ref low, (ulong)(uint)space.Width); Mix(ref high, ref low, (ulong)(uint)space.Height);
        }
        return new StateKey(high, low);
    }

    private static void Mix(ref ulong high, ref ulong low, ulong value)
    {
        high ^= value + 0x9E3779B97F4A7C15UL + (high << 6) + (high >> 2);
        high *= 1_099_511_628_211UL;
        low ^= value + 0x517CC1B727220A95UL + (low << 7) + (low >> 3);
        low *= 0xC2B2AE3D27D4EB4FUL;
    }

    private static string ItemKey(PackingItemUnit item) => $"{item.ItemTypeId:N}:{item.Sequence:D8}:{item.InstanceId:N}";
    private static long Overlap(int a, int lengthA, int b, int lengthB) => Math.Max(0, Math.Min(a + lengthA, b + lengthB) - Math.Max(a, b));
    private static Rectangle2 Intersect(Rectangle2 first, Rectangle2 second) => new(Math.Max(first.Left, second.Left), Math.Max(first.Bottom, second.Bottom), Math.Min(first.Right, second.Right), Math.Min(first.Top, second.Top));

    private sealed record PackingState(List<PackedItem> Packed, List<PackingItemUnit> Remaining, List<SkuState> RemainingSkus, List<Point3> ExtremePoints, List<EmptySpace> EmptySpaces, double CurrentWeight, int MaxX, int MaxY, int MaxZ, long PackedVolume);
    private sealed record Candidate(PackingItemUnit Item, ItemSignature Signature, Point3 Point, PlacementShape Shape, StabilityMetrics Stability, List<EmptySpace> NextSpaces, CandidateScore Score);
    private sealed record BeamNode(PackingState State, Candidate First, double AccumulatedScore);
    private sealed class RunMetrics { public long CandidateEvaluations; public long BeamNodesExpanded; public long CacheHits; public int PeakExtremePoints = 1; public int PeakEmptySpaces = 1; public bool TimeBudgetReached; public int BlockPlacements; public int ItemsPackedAsBlocks; public int LocalRepairAttempts; public int LocalRepairSuccesses; }
    private readonly record struct StabilityMetrics(double SupportRatio, double MinimumQuadrantRatio, bool CenterSupported);
    private readonly record struct Rectangle2(double Left, double Bottom, double Right, double Top) { public double Area => Math.Max(0, Right - Left) * Math.Max(0, Top - Bottom); }
    private readonly record struct CandidateScore(double Value, long WastedVolume, long NegativeFutureFitVolume, int Fragmentation, int Top, int Z, int Y, int X, string Rotation) : IComparable<CandidateScore>
    {
        public int CompareTo(CandidateScore other)
        {
            var value = Value.CompareTo(other.Value); if (value != 0) return value;
            value = WastedVolume.CompareTo(other.WastedVolume); if (value != 0) return value;
            value = NegativeFutureFitVolume.CompareTo(other.NegativeFutureFitVolume); if (value != 0) return value;
            value = Fragmentation.CompareTo(other.Fragmentation); if (value != 0) return value;
            value = Top.CompareTo(other.Top); if (value != 0) return value;
            value = Z.CompareTo(other.Z); if (value != 0) return value;
            value = Y.CompareTo(other.Y); if (value != 0) return value;
            value = X.CompareTo(other.X); return value != 0 ? value : string.CompareOrdinal(Rotation, other.Rotation);
        }
    }
    private readonly record struct ItemSignature(int Length, int Width, int Height, bool AllowRotation, long WeightBits) : IComparable<ItemSignature>
    {
        public static ItemSignature From(PackingItemUnit item) => new(item.Length, item.Width, item.Height, item.AllowRotation, BitConverter.DoubleToInt64Bits(item.WeightKg));
        public int CompareTo(ItemSignature other) { var v=Length.CompareTo(other.Length); if(v!=0)return v; v=Width.CompareTo(other.Width); if(v!=0)return v; v=Height.CompareTo(other.Height); if(v!=0)return v; v=AllowRotation.CompareTo(other.AllowRotation); return v!=0?v:WeightBits.CompareTo(other.WeightBits); }
    }
    private sealed record SkuState(ItemSignature Signature, PackingItemUnit Representative, int Count, IReadOnlyList<OrientedSize> Orientations);
    private sealed record PlacementShape(OrientedSize UnitSize, int CountX, int CountY, int CountZ)
    {
        public int ItemCount => CountX * CountY * CountZ;
        public long Volume => UnitSize.Volume * ItemCount;
        public OrientedSize Size => new(UnitSize.Length * CountX, UnitSize.Width * CountY, UnitSize.Height * CountZ,
            ItemCount == 1 ? UnitSize.Rotation : $"Block[{CountX}x{CountY}x{CountZ}]:{UnitSize.Rotation}");
    }
    private readonly record struct StateKey(ulong High, ulong Low);
}
