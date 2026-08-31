using BinPacking.Web.Models;

namespace BinPacking.Web.Algorithms;

/// <summary>
/// Deterministic largest-first extreme-point heuristic. Coordinates use
/// X=length, Y=width and Z=height. The algorithm is independent of rendering.
/// </summary>
public sealed class ExtremePointPackingAlgorithm : IPackingAlgorithm
{
    public PackingAttempt Pack(BoxType box, IReadOnlyList<PackingItemUnit> items)
    {
        var packed = new List<PackedItem>();
        var unpacked = new List<PackingItemUnit>();
        var points = new List<Point3> { new(0, 0, 0) };
        var currentWeight = 0d;

        var ordered = items
            .OrderByDescending(item => item.Volume)
            .ThenByDescending(item => Math.Max(item.Length, Math.Max(item.Width, item.Height)))
            .ThenBy(item => item.ItemTypeId)
            .ThenBy(item => item.Sequence)
            .ToList();

        foreach (var item in ordered)
        {
            if (box.MaxWeightKg is { } capacity && currentWeight + item.WeightKg > capacity + 1e-9)
            {
                unpacked.Add(item);
                continue;
            }

            Candidate? best = null;
            foreach (var point in points)
            {
                foreach (var size in GetOrientations(item))
                {
                    if (!FitsInside(box, point, size) || OverlapsAny(point, size, packed))
                    {
                        continue;
                    }

                    var score = Score(point, size, packed);
                    if (best is null || score.CompareTo(best.Score) < 0)
                    {
                        best = new Candidate(point, size, score);
                    }
                }
            }

            if (best is null)
            {
                unpacked.Add(item);
                continue;
            }

            var placed = new PackedItem
            {
                InstanceId = item.InstanceId,
                ItemTypeId = item.ItemTypeId,
                Name = item.Name,
                Sequence = item.Sequence,
                X = best.Point.X,
                Y = best.Point.Y,
                Z = best.Point.Z,
                Length = best.Size.Length,
                Width = best.Size.Width,
                Height = best.Size.Height,
                OriginalLength = item.Length,
                OriginalWidth = item.Width,
                OriginalHeight = item.Height,
                Rotation = best.Size.Rotation,
                WeightKg = item.WeightKg
            };

            packed.Add(placed);
            currentWeight += item.WeightKg;
            AddExtremePoints(points, placed, box, packed);
        }

        return new PackingAttempt(box, packed, unpacked);
    }

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

    private static PlacementScore Score(Point3 point, OrientedSize size, IReadOnlyList<PackedItem> packed)
    {
        var maxX = Math.Max(point.X + size.Length, packed.Count == 0 ? 0 : packed.Max(p => p.X + p.Length));
        var maxY = Math.Max(point.Y + size.Width, packed.Count == 0 ? 0 : packed.Max(p => p.Y + p.Width));
        var maxZ = Math.Max(point.Z + size.Height, packed.Count == 0 ? 0 : packed.Max(p => p.Z + p.Height));
        var envelope = (long)maxX * maxY * maxZ;
        var contact = ContactArea(point, size, packed);
        return new PlacementScore(point.Z + size.Height, envelope, -contact, point.Z, point.Y, point.X);
    }

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
        points.AddRange(unique.Where(candidate =>
            !unique.Any(other => other != candidate &&
                                 other.X <= candidate.X &&
                                 other.Y <= candidate.Y &&
                                 other.Z <= candidate.Z)));
    }

    private static bool IsStrictlyInside(Point3 point, PackedItem item) =>
        point.X >= item.X && point.X < item.X + item.Length &&
        point.Y >= item.Y && point.Y < item.Y + item.Width &&
        point.Z >= item.Z && point.Z < item.Z + item.Height;

    private sealed record Candidate(Point3 Point, OrientedSize Size, PlacementScore Score);

    private readonly record struct PlacementScore(
        int Top,
        long Envelope,
        long NegativeContact,
        int Z,
        int Y,
        int X) : IComparable<PlacementScore>
    {
        public int CompareTo(PlacementScore other)
        {
            var comparisons = new[]
            {
                Top.CompareTo(other.Top),
                Envelope.CompareTo(other.Envelope),
                NegativeContact.CompareTo(other.NegativeContact),
                Z.CompareTo(other.Z),
                Y.CompareTo(other.Y),
                X.CompareTo(other.X)
            };
            return comparisons.FirstOrDefault(value => value != 0);
        }
    }
}
