using BinPacking.Web.Models;

namespace BinPacking.Web.Algorithms;

public readonly record struct EmptySpace(int X, int Y, int Z, int Length, int Width, int Height)
{
    public int Right => X + Length;
    public int Back => Y + Width;
    public int Top => Z + Height;
    public long Volume => (long)Length * Width * Height;

    public bool Contains(Point3 point, OrientedSize size) =>
        point.X >= X && point.Y >= Y && point.Z >= Z &&
        point.X + size.Length <= Right && point.Y + size.Width <= Back && point.Z + size.Height <= Top;

    public bool Contains(EmptySpace other) =>
        other.X >= X && other.Y >= Y && other.Z >= Z &&
        other.Right <= Right && other.Back <= Back && other.Top <= Top;
}

public sealed class EmptySpaceManager
{
    public List<EmptySpace> Create(BoxType box) => [new(0, 0, 0, box.Length, box.Width, box.Height)];

    public List<EmptySpace> Place(
        IReadOnlyList<EmptySpace> current,
        Point3 point,
        OrientedSize size,
        IReadOnlyList<IReadOnlyList<OrientedSize>> remainingOrientations,
        int maximumCount)
    {
        var result = new List<EmptySpace>(Math.Min(maximumCount * 2, current.Count + 12));
        var itemRight = point.X + size.Length;
        var itemBack = point.Y + size.Width;
        var itemTop = point.Z + size.Height;

        foreach (var space in current)
        {
            if (!Intersects(space, point, size))
            {
                result.Add(space);
                continue;
            }

            Add(result, space.X, space.Y, space.Z, point.X - space.X, space.Width, space.Height);
            Add(result, itemRight, space.Y, space.Z, space.Right - itemRight, space.Width, space.Height);
            Add(result, space.X, space.Y, space.Z, space.Length, point.Y - space.Y, space.Height);
            Add(result, space.X, itemBack, space.Z, space.Length, space.Back - itemBack, space.Height);
            Add(result, space.X, space.Y, space.Z, space.Length, space.Width, point.Z - space.Z);
            Add(result, space.X, space.Y, itemTop, space.Length, space.Width, space.Top - itemTop);
        }

        return Prune(result, remainingOrientations, maximumCount);
    }

    public static bool CanFitAny(EmptySpace space, IReadOnlyList<IReadOnlyList<OrientedSize>> orientations)
    {
        for (var itemIndex = 0; itemIndex < orientations.Count; itemIndex++)
        {
            var sizes = orientations[itemIndex];
            for (var sizeIndex = 0; sizeIndex < sizes.Count; sizeIndex++)
            {
                var size = sizes[sizeIndex];
                if (size.Length <= space.Length && size.Width <= space.Width && size.Height <= space.Height)
                    return true;
            }
        }

        return false;
    }

    public static int CountFittingTypes(EmptySpace space, IReadOnlyList<IReadOnlyList<OrientedSize>> orientations)
    {
        var count = 0;
        for (var itemIndex = 0; itemIndex < orientations.Count; itemIndex++)
        {
            var sizes = orientations[itemIndex];
            for (var sizeIndex = 0; sizeIndex < sizes.Count; sizeIndex++)
            {
                var size = sizes[sizeIndex];
                if (size.Length > space.Length || size.Width > space.Width || size.Height > space.Height) continue;
                count++;
                break;
            }
        }

        return count;
    }

    public static IEnumerable<EmptySpace> PartitionResidual(EmptySpace space, Point3 point, OrientedSize size)
    {
        var right = point.X + size.Length;
        var back = point.Y + size.Width;
        var top = point.Z + size.Height;
        if (point.X > space.X) yield return new(space.X, space.Y, space.Z, point.X - space.X, space.Width, space.Height);
        if (right < space.Right) yield return new(right, space.Y, space.Z, space.Right - right, space.Width, space.Height);
        var middleLength = Math.Min(space.Right, right) - Math.Max(space.X, point.X);
        if (middleLength <= 0) yield break;
        var middleX = Math.Max(space.X, point.X);
        if (point.Y > space.Y) yield return new(middleX, space.Y, space.Z, middleLength, point.Y - space.Y, space.Height);
        if (back < space.Back) yield return new(middleX, back, space.Z, middleLength, space.Back - back, space.Height);
        var middleWidth = Math.Min(space.Back, back) - Math.Max(space.Y, point.Y);
        if (middleWidth <= 0) yield break;
        var middleY = Math.Max(space.Y, point.Y);
        if (point.Z > space.Z) yield return new(middleX, middleY, space.Z, middleLength, middleWidth, point.Z - space.Z);
        if (top < space.Top) yield return new(middleX, middleY, top, middleLength, middleWidth, space.Top - top);
    }

    private static List<EmptySpace> Prune(
        List<EmptySpace> spaces,
        IReadOnlyList<IReadOnlyList<OrientedSize>> remainingOrientations,
        int maximumCount)
    {
        spaces.Sort(CompareCanonical);
        var unique = new List<EmptySpace>(spaces.Count);
        EmptySpace? previous = null;
        foreach (var space in spaces)
        {
            if (space.Length <= 0 || space.Width <= 0 || space.Height <= 0 || space == previous) continue;
            if (remainingOrientations.Count > 0 && !CanFitAny(space, remainingOrientations)) continue;
            unique.Add(space);
            previous = space;
        }

        var maximal = new List<EmptySpace>(unique.Count);
        for (var index = 0; index < unique.Count; index++)
        {
            var contained = false;
            for (var other = 0; other < unique.Count; other++)
            {
                if (index == other || unique[other].Volume < unique[index].Volume) continue;
                if (!unique[other].Contains(unique[index])) continue;
                contained = true;
                break;
            }
            if (!contained) maximal.Add(unique[index]);
        }

        if (maximal.Count > maximumCount)
        {
            maximal = maximal
                .OrderByDescending(space => CountFittingTypes(space, remainingOrientations))
                .ThenByDescending(space => space.Volume)
                .ThenBy(space => space.Z).ThenBy(space => space.Y).ThenBy(space => space.X)
                .Take(maximumCount)
                .ToList();
        }

        maximal.Sort(CompareCanonical);
        return maximal;
    }

    private static int CompareCanonical(EmptySpace first, EmptySpace second)
    {
        var value = first.X.CompareTo(second.X);
        if (value != 0) return value;
        value = first.Y.CompareTo(second.Y);
        if (value != 0) return value;
        value = first.Z.CompareTo(second.Z);
        if (value != 0) return value;
        value = first.Length.CompareTo(second.Length);
        if (value != 0) return value;
        value = first.Width.CompareTo(second.Width);
        return value != 0 ? value : first.Height.CompareTo(second.Height);
    }

    private static bool Intersects(EmptySpace space, Point3 point, OrientedSize size) =>
        point.X < space.Right && point.X + size.Length > space.X &&
        point.Y < space.Back && point.Y + size.Width > space.Y &&
        point.Z < space.Top && point.Z + size.Height > space.Z;

    private static void Add(List<EmptySpace> target, int x, int y, int z, int length, int width, int height)
    {
        if (length > 0 && width > 0 && height > 0) target.Add(new EmptySpace(x, y, z, length, width, height));
    }
}
