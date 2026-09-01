using BinPacking.Web.Models;
using System.Diagnostics;

namespace BinPacking.Web.Algorithms;

/// <summary>
/// Deterministic multi-strategy extreme-point heuristic with deferred-item
/// backfill and projected coordinates. Coordinates use X=length, Y=width and
/// Z=height. The algorithm is independent of rendering.
/// </summary>
public sealed class ExtremePointPackingAlgorithm : IPackingAlgorithm
{
    // Elevated items must have broad, balanced support and their center of mass
    // projected onto a supporting surface. This avoids physically valid-looking
    // placements that would tip or load a small corner during transportation.
    private const double MinimumSupportRatio = 0.90;
    private const double MinimumQuadrantSupportRatio = 0.70;

    public PackingAttempt Pack(BoxType box, IReadOnlyList<PackingItemUnit> items)
    {
        var stopwatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var metrics = new RunMetrics();
        var orderings = BuildOrderings(items);
        var volumeRatio = items.Sum(item => item.Volume) / (double)box.Volume;
        var useDeepSearch = items.Count >= 8 && volumeRatio is >= 0.55 and <= 1.25;
        var attempts = useDeepSearch
            ? orderings
                .Select(ordering => PackOrdered(box, ordering, PlacementPreference.LowestTop, metrics))
                .Concat(orderings.Take(7)
                    .Select(ordering => PackOrdered(box, ordering, PlacementPreference.CompactEnvelope, metrics)))
                .Concat(orderings.Take(7)
                    .Select(ordering => PackOrdered(box, ordering, PlacementPreference.MaximumContact, metrics)))
                .Concat(Enumerable.Range(1, 5)
                    .Select(interval => PackAdaptive(box, items, interval, false, PlacementPreference.LowestTop, metrics)))
                .Append(PackAdaptive(box, items, 3, true, PlacementPreference.MaximumContact, metrics))
            : orderings.Take(7)
                .Select(ordering => PackOrdered(box, ordering, PlacementPreference.LowestTop, metrics))
                .Append(PackAdaptive(box, items, 3, false, PlacementPreference.MaximumContact, metrics));
        var result = attempts
            .OrderByDescending(attempt => attempt.PackedVolume)
            .ThenByDescending(attempt => attempt.PackedItems.Count)
            .ThenBy(attempt => attempt.PackedItems.Select(item => item.Z + item.Height).DefaultIfEmpty(0).Max())
            .First();
        stopwatch.Stop();
        return result with
        {
            Diagnostics = new PackingDiagnostics
            {
                AlgorithmName = nameof(ExtremePointPackingAlgorithm),
                SearchMode = useDeepSearch ? "DeepMultiStrategy" : "FastMultiStrategy",
                CalculationTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                CandidateEvaluations = metrics.CandidateEvaluations,
                ExtremePointCount = metrics.PeakExtremePointCount,
                ApproximateAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore)
            }
        };
    }

    private PackingAttempt PackOrdered(
        BoxType box,
        IReadOnlyList<PackingItemUnit> ordering,
        PlacementPreference preference,
        RunMetrics metrics)
    {
        var packed = new List<PackedItem>();
        var points = new List<Point3> { new(0, 0, 0) };
        var currentWeight = 0d;
        var maxX = 0;
        var maxY = 0;
        var maxZ = 0;

        var remaining = ordering.ToList();

        while (remaining.Count > 0)
        {
            var deferred = new List<PackingItemUnit>();
            var placedThisPass = false;

            foreach (var item in remaining)
            {
                if (box.MaxWeightKg is { } capacity && currentWeight + item.WeightKg > capacity + 1e-9)
                {
                    deferred.Add(item);
                    continue;
                }

                var best = FindBestCandidate(box, item, points, packed, false, preference, metrics, maxX, maxY, maxZ);

                if (best is null)
                {
                    deferred.Add(item);
                    continue;
                }

                var placed = CreatePackedItem(item, best);

                packed.Add(placed);
                currentWeight += item.WeightKg;
                maxX = Math.Max(maxX, placed.X + placed.Length);
                maxY = Math.Max(maxY, placed.Y + placed.Width);
                maxZ = Math.Max(maxZ, placed.Z + placed.Height);
                placedThisPass = true;
                AddExtremePoints(points, placed, box, packed);
                metrics.PeakExtremePointCount = Math.Max(metrics.PeakExtremePointCount, points.Count);
            }

            if (!placedThisPass)
            {
                return new PackingAttempt(box, packed, deferred);
            }

            remaining = deferred;
        }

        return new PackingAttempt(box, packed, []);
    }

    private PackingAttempt PackAdaptive(
        BoxType box,
        IReadOnlyList<PackingItemUnit> items,
        int constrainedInterval,
        bool useProjectedPoints,
        PlacementPreference preference,
        RunMetrics metrics)
    {
        var packed = new List<PackedItem>();
        var points = new List<Point3> { new(0, 0, 0) };
        var remaining = items.ToList();
        var currentWeight = 0d;
        var elevatedPlacements = 0;
        var maxX = 0;
        var maxY = 0;
        var maxZ = 0;

        while (remaining.Count > 0)
        {
            AdaptiveChoice? bestChoice = null;
            foreach (var item in remaining
                         .GroupBy(candidate => candidate.ItemTypeId)
                         .Select(group => group.OrderBy(candidate => candidate.Sequence).First()))
            {
                if (box.MaxWeightKg is { } capacity && currentWeight + item.WeightKg > capacity + 1e-9)
                    continue;

                var candidate = FindBestCandidate(box, item, points, packed, useProjectedPoints, preference, metrics, maxX, maxY, maxZ);
                if (candidate is null) continue;

                var choice = new AdaptiveChoice(item, candidate);
                var preferConstrained = elevatedPlacements % constrainedInterval == 0;
                if (bestChoice is null || CompareAdaptive(choice, bestChoice, preferConstrained) < 0)
                    bestChoice = choice;
            }

            if (bestChoice is null) break;

            var placed = CreatePackedItem(bestChoice.Item, bestChoice.Candidate);
            packed.Add(placed);
            currentWeight += bestChoice.Item.WeightKg;
            maxX = Math.Max(maxX, placed.X + placed.Length);
            maxY = Math.Max(maxY, placed.Y + placed.Width);
            maxZ = Math.Max(maxZ, placed.Z + placed.Height);
            if (bestChoice.Candidate.Point.Z > 0) elevatedPlacements++;
            remaining.Remove(bestChoice.Item);
            AddExtremePoints(points, placed, box, packed);
            metrics.PeakExtremePointCount = Math.Max(metrics.PeakExtremePointCount, points.Count);
        }

        return new PackingAttempt(box, packed, remaining);
    }

    private static int CompareAdaptive(
        AdaptiveChoice first,
        AdaptiveChoice second,
        bool preferConstrained)
    {
        var baseHeight = first.Candidate.Point.Z.CompareTo(second.Candidate.Point.Z);
        if (baseHeight != 0) return baseHeight;

        if (first.Candidate.Point.Z == 0)
        {
            var floorVolume = second.Item.Volume.CompareTo(first.Item.Volume);
            if (floorVolume != 0) return floorVolume;
        }
        else if (preferConstrained)
        {
            var constrained = first.Item.AllowRotation.CompareTo(second.Item.AllowRotation);
            if (constrained != 0) return constrained;
        }

        var volume = second.Item.Volume.CompareTo(first.Item.Volume);
        if (volume != 0) return volume;

        var candidateScore = first.Candidate.Score.CompareTo(second.Candidate.Score);
        if (candidateScore != 0) return candidateScore;
        return first.Item.ItemTypeId.CompareTo(second.Item.ItemTypeId);
    }

    private static Candidate? FindBestCandidate(
        BoxType box,
        PackingItemUnit item,
        IReadOnlyList<Point3> points,
        IReadOnlyList<PackedItem> packed,
        bool useProjectedPoints,
        PlacementPreference preference,
        RunMetrics metrics,
        int maxX,
        int maxY,
        int maxZ)
    {
        Candidate? best = null;
        foreach (var size in metrics.GetOrientations(item))
        foreach (var point in useProjectedPoints
                     ? points.Concat(ProjectCandidatePoints(box, size, points, packed)).Distinct()
                     : points)
        {
            metrics.CandidateEvaluations++;
            if (!FitsInside(box, point, size) || OverlapsAny(point, size, packed)) continue;

            var stability = MeasureStability(point, size, packed);
            if (!IsStable(stability)) continue;

            var score = Score(point, size, packed, stability, preference, maxX, maxY, maxZ);
            if (best is null || score.CompareTo(best.Score) < 0)
                best = new Candidate(point, size, score, stability);
        }

        return best;
    }

    private static IEnumerable<Point3> ProjectCandidatePoints(
        BoxType box,
        OrientedSize size,
        IReadOnlyList<Point3> points,
        IReadOnlyList<PackedItem> packed)
    {
        var zLevels = points.Select(point => point.Z).Distinct().Order();
        foreach (var z in zLevels)
        {
            if (z + size.Height > box.Height) continue;
            var projected = new HashSet<Point3>
            {
                new(0, 0, z),
                new(box.Length - size.Length, 0, z),
                new(0, box.Width - size.Width, z),
                new(box.Length - size.Length, box.Width - size.Width, z)
            };
            foreach (var point in points)
            {
                if (point.Z != z) continue;
                projected.Add(point);
                projected.Add(new Point3(box.Length - size.Length, point.Y, z));
                projected.Add(new Point3(point.X, box.Width - size.Width, z));
            }
            foreach (var item in packed)
            {
                if (z < item.Z + item.Height && z + size.Height > item.Z)
                {
                    AddProjectedPairs(projected, item, size, z);
                }
                if (z != 0 && item.Z + item.Height == z)
                {
                    projected.Add(new Point3(item.X, item.Y, z));
                    projected.Add(new Point3(item.X + item.Length - size.Length, item.Y, z));
                    projected.Add(new Point3(item.X, item.Y + item.Width - size.Width, z));
                    projected.Add(new Point3(item.X + item.Length - size.Length, item.Y + item.Width - size.Width, z));
                }
            }

            foreach (var point in projected
                         .Where(point => point.X >= 0 && point.Y >= 0 &&
                                         point.X + size.Length <= box.Length && point.Y + size.Width <= box.Width)
                         .OrderBy(point => point.Y).ThenBy(point => point.X))
                yield return point;
        }
    }

    private static void AddProjectedPairs(HashSet<Point3> target, PackedItem item, OrientedSize size, int z)
    {
        var left = item.X - size.Length;
        var right = item.X + item.Length;
        var front = item.Y - size.Width;
        var back = item.Y + item.Width;
        target.Add(new Point3(left, item.Y, z));
        target.Add(new Point3(left, item.Y + item.Width - size.Width, z));
        target.Add(new Point3(right, item.Y, z));
        target.Add(new Point3(right, item.Y + item.Width - size.Width, z));
        target.Add(new Point3(item.X, front, z));
        target.Add(new Point3(item.X + item.Length - size.Length, front, z));
        target.Add(new Point3(item.X, back, z));
        target.Add(new Point3(item.X + item.Length - size.Length, back, z));
    }

    private static PackedItem CreatePackedItem(PackingItemUnit item, Candidate candidate) => new()
    {
        InstanceId = item.InstanceId,
        ItemTypeId = item.ItemTypeId,
        Name = item.Name,
        Sequence = item.Sequence,
        X = candidate.Point.X,
        Y = candidate.Point.Y,
        Z = candidate.Point.Z,
        Length = candidate.Size.Length,
        Width = candidate.Size.Width,
        Height = candidate.Size.Height,
        OriginalLength = item.Length,
        OriginalWidth = item.Width,
        OriginalHeight = item.Height,
        Rotation = candidate.Size.Rotation,
        WeightKg = item.WeightKg,
        Color = item.Color,
        SupportPercent = Math.Round(candidate.Stability.SupportRatio * 100, 2)
    };

    private static IReadOnlyList<IReadOnlyList<PackingItemUnit>> BuildOrderings(
        IReadOnlyList<PackingItemUnit> items)
    {
        var tieBreaker = (PackingItemUnit item) => item.ItemTypeId;
        var orderings = new List<IReadOnlyList<PackingItemUnit>>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        void AddOrdering(IEnumerable<PackingItemUnit> source)
        {
            var ordering = source.ToArray();
            var signature = string.Join(',', ordering.Select(item => item.InstanceId));
            if (signatures.Add(signature)) orderings.Add(ordering);
        }

        AddOrdering(items.OrderByDescending(item => item.Volume)
            .ThenByDescending(LongestSide)
            .ThenBy(tieBreaker)
            .ThenBy(item => item.Sequence));
        AddOrdering(items.OrderBy(item => item.AllowRotation)
            .ThenByDescending(item => item.Volume)
            .ThenBy(tieBreaker)
            .ThenBy(item => item.Sequence));
        AddOrdering(items.OrderByDescending(item => (long)item.Length * item.Width)
            .ThenByDescending(item => item.Volume)
            .ThenBy(tieBreaker)
            .ThenBy(item => item.Sequence));
        AddOrdering(items.OrderByDescending(item => item.Height)
            .ThenByDescending(item => item.Volume)
            .ThenBy(tieBreaker)
            .ThenBy(item => item.Sequence));
        AddOrdering(items.OrderByDescending(ShortestSide)
            .ThenByDescending(item => item.Volume)
            .ThenBy(tieBreaker)
            .ThenBy(item => item.Sequence));

        var canonical = items
            .OrderBy(item => item.ItemTypeId)
            .ThenBy(item => item.Sequence)
            .ToArray();
        var groups = canonical.GroupBy(item => item.ItemTypeId).ToArray();
        AddOrdering(RoundRobin(groups.OrderByDescending(group => group.First().Volume).ToArray()));
        AddOrdering(RoundRobin(groups.OrderBy(group => group.First().AllowRotation)
            .ThenByDescending(group => group.First().Volume)
            .ToArray()));
        var searchRounds = items.Count >= 20 ? 8 : 4;
        for (var round = 0; round < searchRounds; round++)
        {
            var random = new Random(17_071 + round * 7_919);
            var shuffledGroups = Shuffle(groups, random);
            AddOrdering(shuffledGroups.SelectMany(group => group));
            AddOrdering(RoundRobin(shuffledGroups));
            AddOrdering(Shuffle(canonical, random));
        }

        return orderings;
    }

    private static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> source, Random random)
    {
        var result = source.ToArray();
        for (var index = result.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (result[index], result[other]) = (result[other], result[index]);
        }

        return result;
    }

    private static IReadOnlyList<PackingItemUnit> RoundRobin(
        IReadOnlyList<IGrouping<Guid, PackingItemUnit>> groups)
    {
        var queues = groups
            .Select(group => new Queue<PackingItemUnit>(group.OrderBy(item => item.Sequence)))
            .ToArray();
        var result = new List<PackingItemUnit>(queues.Sum(queue => queue.Count));
        while (queues.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (queue.Count > 0) result.Add(queue.Dequeue());
            }
        }

        return result;
    }

    private static int LongestSide(PackingItemUnit item) =>
        Math.Max(item.Length, Math.Max(item.Width, item.Height));

    private static int ShortestSide(PackingItemUnit item) =>
        Math.Min(item.Length, Math.Min(item.Width, item.Height));

    private static IReadOnlyList<OrientedSize> GetOrientations(PackingItemUnit item)
    {
        if (!item.AllowRotation)
        {
            return [new(item.Length, item.Width, item.Height, "L-W-H")];
        }

        var candidates = new[]
        {
            new OrientedSize(item.Length, item.Width, item.Height, "L-W-H"),
            new OrientedSize(item.Length, item.Height, item.Width, "L-H-W"),
            new OrientedSize(item.Width, item.Length, item.Height, "W-L-H"),
            new OrientedSize(item.Width, item.Height, item.Length, "W-H-L"),
            new OrientedSize(item.Height, item.Length, item.Width, "H-L-W"),
            new OrientedSize(item.Height, item.Width, item.Length, "H-W-L")
        };

        return candidates
            .DistinctBy(size => (size.Length, size.Width, size.Height))
            .ToArray();
    }

    private static bool FitsInside(BoxType box, Point3 point, OrientedSize size) =>
        point.X + size.Length <= box.Length &&
        point.Y + size.Width <= box.Width &&
        point.Z + size.Height <= box.Height;

    private static bool OverlapsAny(Point3 point, OrientedSize size, IEnumerable<PackedItem> packed)
    {
        foreach (var other in packed)
        {
            if (point.X < other.X + other.Length && point.X + size.Length > other.X &&
                point.Y < other.Y + other.Width && point.Y + size.Width > other.Y &&
                point.Z < other.Z + other.Height && point.Z + size.Height > other.Z) return true;
        }
        return false;
    }

    private static PlacementScore Score(
        Point3 point,
        OrientedSize size,
        IReadOnlyList<PackedItem> packed,
        StabilityMetrics stability,
        PlacementPreference preference,
        int currentMaxX,
        int currentMaxY,
        int currentMaxZ)
    {
        var maxX = Math.Max(point.X + size.Length, currentMaxX);
        var maxY = Math.Max(point.Y + size.Width, currentMaxY);
        var maxZ = Math.Max(point.Z + size.Height, currentMaxZ);
        var envelope = (long)maxX * maxY * maxZ;
        var contact = ContactArea(point, size, packed);
        var supportBasisPoints = (int)Math.Round(stability.SupportRatio * 10_000);
        var quadrantBasisPoints = (int)Math.Round(stability.MinimumQuadrantRatio * 10_000);
        var top = point.Z + size.Height;
        var (primary, secondary, tertiary) = preference switch
        {
            PlacementPreference.CompactEnvelope => (envelope, (long)top, -contact),
            PlacementPreference.MaximumContact => (-contact, (long)top, envelope),
            _ => ((long)top, envelope, -contact)
        };
        return new PlacementScore(
            -supportBasisPoints,
            -quadrantBasisPoints,
            primary,
            secondary,
            tertiary,
            point.Z,
            point.Y,
            point.X);
    }

    private static bool IsStable(StabilityMetrics stability) =>
        stability.CenterSupported &&
        stability.SupportRatio + 1e-9 >= MinimumSupportRatio &&
        stability.MinimumQuadrantRatio + 1e-9 >= MinimumQuadrantSupportRatio;

    private static StabilityMetrics MeasureStability(
        Point3 point,
        OrientedSize size,
        IReadOnlyList<PackedItem> packed)
    {
        if (point.Z == 0)
        {
            return new StabilityMetrics(1, 1, true);
        }

        var footprint = new Rectangle2(point.X, point.Y, point.X + size.Length, point.Y + size.Width);
        var supportingSurfaces = packed
            .Where(item => item.Z + item.Height == point.Z)
            .Select(item => Intersect(
                footprint,
                new Rectangle2(item.X, item.Y, item.X + item.Length, item.Y + item.Width)))
            .Where(rectangle => rectangle.Area > 0)
            .ToArray();

        if (supportingSurfaces.Length == 0)
        {
            return new StabilityMetrics(0, 0, false);
        }
        if (supportingSurfaces.Length == 1 && supportingSurfaces[0].Area >= footprint.Area - 1e-9)
        {
            return new StabilityMetrics(1, 1, true);
        }

        var supportRatio = CoveredArea(footprint, supportingSurfaces) / footprint.Area;
        var centerX = (footprint.Left + footprint.Right) / 2d;
        var centerY = (footprint.Bottom + footprint.Top) / 2d;
        var centerSupported = supportingSurfaces.Any(surface =>
            centerX >= surface.Left && centerX <= surface.Right &&
            centerY >= surface.Bottom && centerY <= surface.Top);

        var quadrants = new[]
        {
            new Rectangle2(footprint.Left, footprint.Bottom, centerX, centerY),
            new Rectangle2(centerX, footprint.Bottom, footprint.Right, centerY),
            new Rectangle2(footprint.Left, centerY, centerX, footprint.Top),
            new Rectangle2(centerX, centerY, footprint.Right, footprint.Top)
        };
        var minimumQuadrantRatio = quadrants.Min(quadrant =>
            quadrant.Area == 0 ? 1 : CoveredArea(quadrant, supportingSurfaces) / quadrant.Area);

        return new StabilityMetrics(supportRatio, minimumQuadrantRatio, centerSupported);
    }

    private static double CoveredArea(Rectangle2 target, IReadOnlyList<Rectangle2> surfaces)
    {
        var clipped = surfaces
            .Select(surface => Intersect(target, surface))
            .Where(rectangle => rectangle.Area > 0)
            .ToArray();
        if (clipped.Length == 0) return 0;

        var xCoordinates = clipped
            .SelectMany(rectangle => new[] { rectangle.Left, rectangle.Right })
            .Append(target.Left)
            .Append(target.Right)
            .Distinct()
            .Order()
            .ToArray();
        var yCoordinates = clipped
            .SelectMany(rectangle => new[] { rectangle.Bottom, rectangle.Top })
            .Append(target.Bottom)
            .Append(target.Top)
            .Distinct()
            .Order()
            .ToArray();

        var area = 0d;
        for (var xIndex = 0; xIndex < xCoordinates.Length - 1; xIndex++)
        for (var yIndex = 0; yIndex < yCoordinates.Length - 1; yIndex++)
        {
            var left = xCoordinates[xIndex];
            var right = xCoordinates[xIndex + 1];
            var bottom = yCoordinates[yIndex];
            var top = yCoordinates[yIndex + 1];
            var centerX = (left + right) / 2d;
            var centerY = (bottom + top) / 2d;
            if (clipped.Any(rectangle =>
                    centerX > rectangle.Left && centerX < rectangle.Right &&
                    centerY > rectangle.Bottom && centerY < rectangle.Top))
            {
                area += (right - left) * (top - bottom);
            }
        }

        return area;
    }

    private static Rectangle2 Intersect(Rectangle2 first, Rectangle2 second) => new(
        Math.Max(first.Left, second.Left),
        Math.Max(first.Bottom, second.Bottom),
        Math.Min(first.Right, second.Right),
        Math.Min(first.Top, second.Top));

    private static long ContactArea(Point3 point, OrientedSize size, IReadOnlyList<PackedItem> packed)
    {
        long area = 0;
        if (point.X == 0) area += (long)size.Width * size.Height;
        if (point.Y == 0) area += (long)size.Length * size.Height;
        if (point.Z == 0) area += (long)size.Length * size.Width;

        foreach (var other in packed)
        {
            if (point.X == other.X + other.Length || point.X + size.Length == other.X)
                area += OverlapLength(point.Y, size.Width, other.Y, other.Width) *
                        OverlapLength(point.Z, size.Height, other.Z, other.Height);
            if (point.Y == other.Y + other.Width || point.Y + size.Width == other.Y)
                area += OverlapLength(point.X, size.Length, other.X, other.Length) *
                        OverlapLength(point.Z, size.Height, other.Z, other.Height);
            if (point.Z == other.Z + other.Height || point.Z + size.Height == other.Z)
                area += OverlapLength(point.X, size.Length, other.X, other.Length) *
                        OverlapLength(point.Y, size.Width, other.Y, other.Width);
        }

        return area;
    }

    private static long OverlapLength(int a, int aLength, int b, int bLength) =>
        Math.Max(0, Math.Min(a + aLength, b + bLength) - Math.Max(a, b));

    private static void AddExtremePoints(
        List<Point3> points,
        PackedItem placed,
        BoxType box,
        IReadOnlyList<PackedItem> packed)
    {
        points.RemoveAll(point => IsStrictlyInside(point, placed));
        points.Add(new Point3(placed.X + placed.Length, placed.Y, placed.Z));
        points.Add(new Point3(placed.X, placed.Y + placed.Width, placed.Z));
        points.Add(new Point3(placed.X, placed.Y, placed.Z + placed.Height));

        var unique = points
            .Where(point => point.X >= 0 && point.Y >= 0 && point.Z >= 0 &&
                            point.X < box.Length && point.Y < box.Width && point.Z < box.Height)
            .Where(point => !packed.Any(item => IsStrictlyInside(point, item)))
            .Distinct()
            .ToList();

        points.Clear();
        points.AddRange(unique.OrderBy(point => point.Z).ThenBy(point => point.Y).ThenBy(point => point.X));
    }

    private static bool IsStrictlyInside(Point3 point, PackedItem item) =>
        point.X >= item.X && point.X < item.X + item.Length &&
        point.Y >= item.Y && point.Y < item.Y + item.Width &&
        point.Z >= item.Z && point.Z < item.Z + item.Height;

    private sealed record Candidate(
        Point3 Point,
        OrientedSize Size,
        PlacementScore Score,
        StabilityMetrics Stability);

    private sealed record AdaptiveChoice(PackingItemUnit Item, Candidate Candidate);

    private sealed class RunMetrics
    {
        private readonly Dictionary<(int Length, int Width, int Height, bool Rotation), IReadOnlyList<OrientedSize>> _orientations = new();
        public long CandidateEvaluations;
        public int PeakExtremePointCount = 1;

        public IReadOnlyList<OrientedSize> GetOrientations(PackingItemUnit item)
        {
            var key = (item.Length, item.Width, item.Height, item.AllowRotation);
            if (_orientations.TryGetValue(key, out var result)) return result;
            result = ExtremePointPackingAlgorithm.GetOrientations(item);
            _orientations[key] = result;
            return result;
        }
    }

    private enum PlacementPreference
    {
        LowestTop,
        CompactEnvelope,
        MaximumContact
    }

    private readonly record struct StabilityMetrics(
        double SupportRatio,
        double MinimumQuadrantRatio,
        bool CenterSupported);

    private readonly record struct Rectangle2(double Left, double Bottom, double Right, double Top)
    {
        public double Area => Math.Max(0, Right - Left) * Math.Max(0, Top - Bottom);
    }

    private readonly record struct PlacementScore(
        int NegativeSupportBasisPoints,
        int NegativeQuadrantBasisPoints,
        long Primary,
        long Secondary,
        long Tertiary,
        int Z,
        int Y,
        int X) : IComparable<PlacementScore>
    {
        public int CompareTo(PlacementScore other)
        {
            var value = NegativeSupportBasisPoints.CompareTo(other.NegativeSupportBasisPoints);
            if (value != 0) return value;
            value = NegativeQuadrantBasisPoints.CompareTo(other.NegativeQuadrantBasisPoints);
            if (value != 0) return value;
            value = Primary.CompareTo(other.Primary);
            if (value != 0) return value;
            value = Secondary.CompareTo(other.Secondary);
            if (value != 0) return value;
            value = Tertiary.CompareTo(other.Tertiary);
            if (value != 0) return value;
            value = Z.CompareTo(other.Z);
            if (value != 0) return value;
            value = Y.CompareTo(other.Y);
            return value != 0 ? value : X.CompareTo(other.X);
        }
    }
}
