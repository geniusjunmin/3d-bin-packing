using BinPacking.Web.Models;

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
        var orderings = BuildOrderings(items);
        var attempts = orderings
            .Select(ordering => PackOrdered(box, ordering, PlacementPreference.LowestTop))
            .Concat(orderings.Take(7)
                .Select(ordering => PackOrdered(box, ordering, PlacementPreference.CompactEnvelope)))
            .Concat(orderings.Take(7)
                .Select(ordering => PackOrdered(box, ordering, PlacementPreference.MaximumContact)))
            .Concat(Enumerable.Range(1, 5)
                .Select(interval => PackAdaptive(box, items, interval, false, PlacementPreference.LowestTop)))
            .Append(PackAdaptive(box, items, 3, true, PlacementPreference.MaximumContact));
        return attempts
            .OrderByDescending(attempt => attempt.PackedVolume)
            .ThenByDescending(attempt => attempt.PackedItems.Count)
            .ThenBy(attempt => attempt.PackedItems.Select(item => item.Z + item.Height).DefaultIfEmpty(0).Max())
            .First();
    }

    private PackingAttempt PackOrdered(
        BoxType box,
        IReadOnlyList<PackingItemUnit> ordering,
        PlacementPreference preference)
    {
        var packed = new List<PackedItem>();
        var points = new List<Point3> { new(0, 0, 0) };
        var currentWeight = 0d;

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

                var best = FindBestCandidate(box, item, points, packed, false, preference);

                if (best is null)
                {
                    deferred.Add(item);
                    continue;
                }

                var placed = CreatePackedItem(item, best);

                packed.Add(placed);
                currentWeight += item.WeightKg;
                placedThisPass = true;
                AddExtremePoints(points, placed, box, packed);
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
        PlacementPreference preference)
    {
        var packed = new List<PackedItem>();
        var points = new List<Point3> { new(0, 0, 0) };
        var remaining = items.ToList();
        var currentWeight = 0d;
        var elevatedPlacements = 0;

        while (remaining.Count > 0)
        {
            AdaptiveChoice? bestChoice = null;
            foreach (var item in remaining
                         .GroupBy(candidate => candidate.ItemTypeId)
                         .Select(group => group.OrderBy(candidate => candidate.Sequence).First()))
            {
                if (box.MaxWeightKg is { } capacity && currentWeight + item.WeightKg > capacity + 1e-9)
                    continue;

                var candidate = FindBestCandidate(box, item, points, packed, useProjectedPoints, preference);
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
            if (bestChoice.Candidate.Point.Z > 0) elevatedPlacements++;
            remaining.Remove(bestChoice.Item);
            AddExtremePoints(points, placed, box, packed);
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
        PlacementPreference preference)
    {
        Candidate? best = null;
        foreach (var size in GetOrientations(item))
        foreach (var point in useProjectedPoints
                     ? points.Concat(ProjectCandidatePoints(box, size, points, packed)).Distinct()
                     : points)
        {
            if (!FitsInside(box, point, size) || OverlapsAny(point, size, packed)) continue;

            var stability = MeasureStability(point, size, packed);
            if (!IsStable(stability)) continue;

            var score = Score(point, size, packed, stability, preference);
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
        foreach (var z in points.Select(point => point.Z).Distinct().Order())
        {
            if (z + size.Height > box.Height) continue;

            var blockers = packed
                .Where(item => z < item.Z + item.Height && z + size.Height > item.Z)
                .ToArray();
            var supporters = z == 0
                ? []
                : packed.Where(item => item.Z + item.Height == z).ToArray();
            var xCoordinates = points.Where(point => point.Z == z).Select(point => point.X)
                .Append(0)
                .Append(box.Length - size.Length)
                .Concat(blockers.SelectMany(item => new[] { item.X + item.Length, item.X - size.Length }))
                .Concat(supporters.SelectMany(item => new[] { item.X, item.X + item.Length - size.Length }))
                .Where(x => x >= 0 && x + size.Length <= box.Length)
                .Distinct()
                .Order()
                .ToArray();
            var yCoordinates = points.Where(point => point.Z == z).Select(point => point.Y)
                .Append(0)
                .Append(box.Width - size.Width)
                .Concat(blockers.SelectMany(item => new[] { item.Y + item.Width, item.Y - size.Width }))
                .Concat(supporters.SelectMany(item => new[] { item.Y, item.Y + item.Width - size.Width }))
                .Where(y => y >= 0 && y + size.Width <= box.Width)
                .Distinct()
                .Order()
                .ToArray();

            foreach (var x in xCoordinates)
            foreach (var y in yCoordinates)
                yield return new Point3(x, y, z);
        }
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

    private static bool OverlapsAny(Point3 point, OrientedSize size, IEnumerable<PackedItem> packed) =>
        packed.Any(other =>
            point.X < other.X + other.Length && point.X + size.Length > other.X &&
            point.Y < other.Y + other.Width && point.Y + size.Width > other.Y &&
            point.Z < other.Z + other.Height && point.Z + size.Height > other.Z);

    private static PlacementScore Score(
        Point3 point,
        OrientedSize size,
        IReadOnlyList<PackedItem> packed,
        StabilityMetrics stability,
        PlacementPreference preference)
    {
        var maxX = Math.Max(point.X + size.Length, packed.Count == 0 ? 0 : packed.Max(p => p.X + p.Length));
        var maxY = Math.Max(point.Y + size.Width, packed.Count == 0 ? 0 : packed.Max(p => p.Y + p.Width));
        var maxZ = Math.Max(point.Z + size.Height, packed.Count == 0 ? 0 : packed.Max(p => p.Z + p.Height));
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
            var comparisons = new[]
            {
                NegativeSupportBasisPoints.CompareTo(other.NegativeSupportBasisPoints),
                NegativeQuadrantBasisPoints.CompareTo(other.NegativeQuadrantBasisPoints),
                Primary.CompareTo(other.Primary),
                Secondary.CompareTo(other.Secondary),
                Tertiary.CompareTo(other.Tertiary),
                Z.CompareTo(other.Z),
                Y.CompareTo(other.Y),
                X.CompareTo(other.X)
            };
            return comparisons.FirstOrDefault(value => value != 0);
        }
    }
}
