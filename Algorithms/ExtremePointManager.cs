using BinPacking.Web.Models;

namespace BinPacking.Web.Algorithms;

public sealed class ExtremePointManager
{
    public List<Point3> Create() => [new(0, 0, 0)];

    public List<Point3> Place(
        IReadOnlyList<Point3> current,
        PackedItem placed,
        BoxType box,
        IReadOnlyList<PackedItem> packed,
        IReadOnlyList<EmptySpace> spaces,
        int maximumCount)
    {
        var candidates = new HashSet<Point3>(current);
        candidates.Add(new Point3(placed.X + placed.Length, placed.Y, placed.Z));
        candidates.Add(new Point3(placed.X, placed.Y + placed.Width, placed.Z));
        candidates.Add(new Point3(placed.X, placed.Y, placed.Z + placed.Height));

        // Geometrically meaningful projections onto neighbouring item/EMS faces.
        foreach (var item in packed)
        {
            candidates.Add(new Point3(placed.X + placed.Length, item.Y, placed.Z));
            candidates.Add(new Point3(item.X, placed.Y + placed.Width, placed.Z));
            candidates.Add(new Point3(item.X, item.Y, placed.Z + placed.Height));
        }
        foreach (var space in spaces)
            candidates.Add(new Point3(space.X, space.Y, space.Z));

        var result = candidates
            .Where(point => point.X >= 0 && point.Y >= 0 && point.Z >= 0 && point.X < box.Length && point.Y < box.Width && point.Z < box.Height)
            .Where(point => !packed.Any(item => IsInside(point, item)))
            .Where(point => spaces.Any(space => point.X >= space.X && point.Y >= space.Y && point.Z >= space.Z && point.X < space.Right && point.Y < space.Back && point.Z < space.Top))
            .OrderBy(point => point.Z).ThenBy(point => point.Y).ThenBy(point => point.X)
            .Take(maximumCount)
            .ToList();
        return result.Count == 0 ? [new Point3(0, 0, 0)] : result;
    }

    private static bool IsInside(Point3 point, PackedItem item) =>
        point.X >= item.X && point.X < item.X + item.Length &&
        point.Y >= item.Y && point.Y < item.Y + item.Width &&
        point.Z >= item.Z && point.Z < item.Z + item.Height;
}
