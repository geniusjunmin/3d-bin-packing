using System.Diagnostics;
using System.Text;
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
        var state = new PackingState(
            [], items.OrderBy(ItemKey).ToList(), _extremePoints.Create(), _emptySpaces.Create(box),
            0, 0, 0, 0, 0);
        var memo = new Dictionary<string, long>(StringComparer.Ordinal);

        while (state.Remaining.Count > 0)
        {
            var candidates = FindCandidates(box, state, orientationCache, metrics, _options.BranchFactor * 2);
            if (candidates.Count == 0) break;

            var selected = candidates[0];
            if (ShouldSearch(state, box, candidates, stopwatch))
            {
                selected = ChooseWithLookahead(box, state, candidates, orientationCache, metrics, memo, stopwatch);
            }

            state = Place(box, state, selected, orientationCache, metrics);
        }

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
                TimeBudgetReached = metrics.TimeBudgetReached
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
                TimeBudgetReached = hybrid.Diagnostics.TimeBudgetReached
            }
        };
    }

    private Candidate ChooseWithLookahead(
        BoxType box,
        PackingState initial,
        IReadOnlyList<Candidate> initialCandidates,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics,
        Dictionary<string, long> memo,
        Stopwatch stopwatch)
    {
        var depthLimit = initial.Remaining.Count <= 6 && _options.SearchMode == PackingSearchMode.Quality
            ? Math.Min(6, initial.Remaining.Count)
            : _options.LookaheadDepth;
        var beam = initialCandidates.Take(_options.BranchFactor)
            .Select(candidate => new BeamNode(Place(box, initial, candidate, orientationCache, metrics), candidate, candidate.Score.Value))
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

                    var next = Place(box, node.State, candidate, orientationCache, metrics);
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
                .ThenByDescending(node => FutureFitVolume(node.State, orientationCache))
                .ThenBy(node => node.AccumulatedScore)
                .ThenBy(node => node.First.Item.ItemTypeId)
                .ThenBy(node => node.First.Item.Sequence)
                .Take(_options.BeamWidth)
                .ToList();
        }

        return beam
            .OrderByDescending(node => node.State.PackedVolume)
            .ThenByDescending(node => FutureFitVolume(node.State, orientationCache))
            .ThenBy(node => node.AccumulatedScore)
            .ThenBy(node => node.First.Score)
            .Select(node => node.First)
            .FirstOrDefault() ?? initialCandidates[0];
    }

    private List<Candidate> FindCandidates(
        BoxType box,
        PackingState state,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics,
        int take)
    {
        var remainingOrientationSets = GetRemainingOrientationSets(state.Remaining, orientationCache);
        var representativeItems = state.Remaining
            .GroupBy(ItemSignature.From)
            .Select(group => group.OrderBy(item => item.Sequence).ThenBy(item => item.InstanceId).First())
            .Select(item => new { Item = item, Difficulty = Difficulty(item, box, state.EmptySpaces, orientationCache) })
            .OrderByDescending(entry => entry.Difficulty)
            .ThenByDescending(entry => entry.Item.Volume)
            .ThenBy(entry => ItemKey(entry.Item))
            .Take(_options.DifficultyTopK)
            .Select(entry => entry.Item)
            .ToArray();
        var candidates = new List<Candidate>(take * 4);

        foreach (var item in representativeItems)
        {
            if (box.MaxWeightKg is { } capacity && state.CurrentWeight + item.WeightKg > capacity + 1e-9) continue;
            var remainingAfter = GetRemainingOrientationSetsAfter(state.Remaining, item, orientationCache);
            foreach (var size in orientationCache[ItemSignature.From(item)])
            {
                var points = CandidatePoints(state, size);
                foreach (var point in points)
                {
                    metrics.CandidateEvaluations++;
                    if (point.X + size.Length > box.Length || point.Y + size.Width > box.Width || point.Z + size.Height > box.Height) continue;
                    if (!state.EmptySpaces.Any(space => space.Contains(point, size))) continue;
                    if (OverlapsAny(point, size, state.Packed)) continue;
                    var stability = MeasureStability(point, size, state.Packed);
                    if (!IsStable(stability)) continue;

                    var nextSpaces = _emptySpaces.Place(state.EmptySpaces, point, size, remainingAfter, _options.MaxEmptySpaces);
                    var score = Score(box, state, item, point, size, stability, nextSpaces, remainingAfter, orientationCache);
                    candidates.Add(new Candidate(item, point, size, stability, nextSpaces, score));
                }
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Item.Volume)
            .ThenBy(candidate => ItemKey(candidate.Item))
            .Take(take)
            .ToList();
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
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache,
        RunMetrics metrics)
    {
        var packedItem = new PackedItem
        {
            InstanceId = candidate.Item.InstanceId,
            ItemTypeId = candidate.Item.ItemTypeId,
            Name = candidate.Item.Name,
            Sequence = candidate.Item.Sequence,
            X = candidate.Point.X,
            Y = candidate.Point.Y,
            Z = candidate.Point.Z,
            Length = candidate.Size.Length,
            Width = candidate.Size.Width,
            Height = candidate.Size.Height,
            OriginalLength = candidate.Item.Length,
            OriginalWidth = candidate.Item.Width,
            OriginalHeight = candidate.Item.Height,
            Rotation = candidate.Size.Rotation,
            WeightKg = candidate.Item.WeightKg,
            Color = candidate.Item.Color,
            SupportPercent = Math.Round(candidate.Stability.SupportRatio * 100, 2)
        };
        var packed = new List<PackedItem>(state.Packed.Count + 1);
        packed.AddRange(state.Packed);
        packed.Add(packedItem);
        var remaining = new List<PackingItemUnit>(state.Remaining.Count - 1);
        var removed = false;
        foreach (var item in state.Remaining)
        {
            if (!removed && item.InstanceId == candidate.Item.InstanceId) { removed = true; continue; }
            remaining.Add(item);
        }
        var points = _extremePoints.Place(state.ExtremePoints, packedItem, box, packed, candidate.NextSpaces, _options.MaxExtremePoints);
        metrics.PeakExtremePoints = Math.Max(metrics.PeakExtremePoints, points.Count);
        metrics.PeakEmptySpaces = Math.Max(metrics.PeakEmptySpaces, candidate.NextSpaces.Count);
        return new PackingState(
            packed, remaining, points, candidate.NextSpaces,
            state.CurrentWeight + candidate.Item.WeightKg,
            Math.Max(state.MaxX, candidate.Point.X + candidate.Size.Length),
            Math.Max(state.MaxY, candidate.Point.Y + candidate.Size.Width),
            Math.Max(state.MaxZ, candidate.Point.Z + candidate.Size.Height),
            state.PackedVolume + candidate.Item.Volume);
    }

    private CandidateScore Score(
        BoxType box,
        PackingState state,
        PackingItemUnit item,
        Point3 point,
        OrientedSize size,
        StabilityMetrics stability,
        IReadOnlyList<EmptySpace> nextSpaces,
        IReadOnlyList<IReadOnlyList<OrientedSize>> remainingOrientations,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> orientationCache)
    {
        var boxVolume = (double)box.Volume;
        var fillRatio = (state.PackedVolume + item.Volume) / boxVolume;
        long wastedVolume = 0;
        var containing = state.EmptySpaces.Where(space => space.Contains(point, size)).OrderBy(space => space.Volume).First();
        foreach (var residual in EmptySpaceManager.PartitionResidual(containing, point, size))
            if (!EmptySpaceManager.CanFitAny(residual, remainingOrientations)) wastedVolume += residual.Volume;

        long futureFitVolume = 0;
        for (var index = 0; index < state.Remaining.Count; index++)
        {
            var remaining = state.Remaining[index];
            if (remaining.InstanceId == item.InstanceId) continue;
            var orientations = orientationCache[ItemSignature.From(remaining)];
            if (nextSpaces.Any(space => orientations.Any(oriented => oriented.Length <= space.Length && oriented.Width <= space.Width && oriented.Height <= space.Height)))
                futureFitVolume += remaining.Volume;
        }

        var remainingVolume = Math.Max(1d, state.Remaining.Sum(remaining => (double)remaining.Volume) - item.Volume);
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
            _options.SupportWeight * stability.SupportRatio;
        return new CandidateScore(value, wastedVolume, -futureFitVolume, nextSpaces.Count, point.Z + size.Height, point.Z, point.Y, point.X, size.Rotation);
    }

    private bool ShouldSearch(PackingState state, BoxType box, IReadOnlyList<Candidate> candidates, Stopwatch stopwatch)
    {
        if (!_options.EnableBeamSearch || _options.LookaheadDepth <= 1 || stopwatch.ElapsedMilliseconds >= _options.TimeBudgetMs) return false;
        var fill = state.PackedVolume / (double)box.Volume;
        var close = candidates.Count > 1 && Math.Abs(candidates[1].Score.Value - candidates[0].Score.Value) < 0.08;
        return fill >= 0.50 || state.Remaining.Count <= 12 || state.EmptySpaces.Count >= 18 || close;
    }

    private double Difficulty(
        PackingItemUnit item,
        BoxType box,
        IReadOnlyList<EmptySpace> spaces,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> cache)
    {
        var orientations = cache[ItemSignature.From(item)];
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

    private static long FutureFitVolume(PackingState state, IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> cache)
    {
        long volume = 0;
        foreach (var item in state.Remaining)
        {
            var sizes = cache[ItemSignature.From(item)];
            if (state.EmptySpaces.Any(space => sizes.Any(size => size.Length <= space.Length && size.Width <= space.Width && size.Height <= space.Height))) volume += item.Volume;
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

    private static List<IReadOnlyList<OrientedSize>> GetRemainingOrientationSets(IReadOnlyList<PackingItemUnit> remaining, IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> cache) =>
        remaining.GroupBy(ItemSignature.From).Select(group => cache[group.Key]).ToList();

    private static List<IReadOnlyList<OrientedSize>> GetRemainingOrientationSetsAfter(
        IReadOnlyList<PackingItemUnit> remaining,
        PackingItemUnit removed,
        IReadOnlyDictionary<ItemSignature, IReadOnlyList<OrientedSize>> cache)
    {
        var signatures = new HashSet<ItemSignature>();
        var skipped = false;
        foreach (var item in remaining)
        {
            if (!skipped && item.InstanceId == removed.InstanceId) { skipped = true; continue; }
            signatures.Add(ItemSignature.From(item));
        }
        return signatures.Order().Select(signature => cache[signature]).ToList();
    }

    private static string BuildStateHash(PackingState state)
    {
        var builder = new StringBuilder(256);
        foreach (var group in state.Remaining.GroupBy(ItemSignature.From).OrderBy(group => group.Key)) builder.Append(group.Key).Append(':').Append(group.Count()).Append(';');
        builder.Append('|').Append(state.PackedVolume).Append('|').Append(Math.Round(state.CurrentWeight, 6)).Append('|');
        foreach (var space in state.EmptySpaces) builder.Append(space.X).Append(',').Append(space.Y).Append(',').Append(space.Z).Append(',').Append(space.Length).Append(',').Append(space.Width).Append(',').Append(space.Height).Append(';');
        return builder.ToString();
    }

    private static string ItemKey(PackingItemUnit item) => $"{item.ItemTypeId:N}:{item.Sequence:D8}:{item.InstanceId:N}";
    private static long Overlap(int a, int lengthA, int b, int lengthB) => Math.Max(0, Math.Min(a + lengthA, b + lengthB) - Math.Max(a, b));
    private static Rectangle2 Intersect(Rectangle2 first, Rectangle2 second) => new(Math.Max(first.Left, second.Left), Math.Max(first.Bottom, second.Bottom), Math.Min(first.Right, second.Right), Math.Min(first.Top, second.Top));

    private sealed record PackingState(List<PackedItem> Packed, List<PackingItemUnit> Remaining, List<Point3> ExtremePoints, List<EmptySpace> EmptySpaces, double CurrentWeight, int MaxX, int MaxY, int MaxZ, long PackedVolume);
    private sealed record Candidate(PackingItemUnit Item, Point3 Point, OrientedSize Size, StabilityMetrics Stability, List<EmptySpace> NextSpaces, CandidateScore Score);
    private sealed record BeamNode(PackingState State, Candidate First, double AccumulatedScore);
    private sealed class RunMetrics { public long CandidateEvaluations; public long BeamNodesExpanded; public long CacheHits; public int PeakExtremePoints = 1; public int PeakEmptySpaces = 1; public bool TimeBudgetReached; }
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
}
