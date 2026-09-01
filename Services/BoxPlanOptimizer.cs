using System.Text;
using BinPacking.Web.Algorithms;
using BinPacking.Web.Models;

namespace BinPacking.Web.Services;

/// <summary>Deterministic depth-two lookahead for mixed box plans.</summary>
public sealed class BoxPlanOptimizer(IPackingAlgorithm algorithm)
{
    private const int BoxBeamWidth = 4;

    public List<PackingAttempt>? BuildPlan(IReadOnlyList<BoxType> sourceBoxes, IReadOnlyList<PackingItemUnit> source)
    {
        var remaining = source.ToList();
        var plan = new List<PackingAttempt>();
        var cache = new Dictionary<string, PackingAttempt>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var boxes = FilterCandidateBoxes(sourceBoxes, remaining);
            var firstSteps = boxes
                .Select(box => Evaluate(box, remaining, cache))
                .Where(attempt => attempt.PackedItems.Count > 0)
                .Select(attempt => new FirstStep(attempt, RemovePacked(remaining, attempt)))
                .ToList();
            if (firstSteps.Count == 0) return null;

            var scored = new List<ScoredStep>(firstSteps.Count);
            foreach (var first in firstSteps)
            {
                if (first.Remaining.Count == 0)
                {
                    scored.Add(new ScoredStep(first.Attempt, plan.Count + 1, plan.Sum(item => item.Box.Volume) + first.Attempt.Box.Volume, 0, 0));
                    continue;
                }

                var secondBoxes = FilterCandidateBoxes(sourceBoxes, first.Remaining);
                var secondCandidates = secondBoxes
                    .Select(box => Evaluate(box, first.Remaining, cache))
                    .Where(attempt => attempt.PackedItems.Count > 0)
                    .OrderByDescending(attempt => attempt.PackedVolume)
                    .ThenBy(attempt => attempt.Box.Volume)
                    .Take(BoxBeamWidth)
                    .ToArray();
                if (secondCandidates.Length == 0) continue;

                ScoredStep? bestContinuation = null;
                foreach (var second in secondCandidates)
                {
                    var afterSecond = RemovePacked(first.Remaining, second);
                    var lowerBound = RemainingBoxLowerBound(afterSecond, sourceBoxes);
                    var minimumVolume = MinimumFeasibleBoxVolume(afterSecond, sourceBoxes);
                    var estimatedCount = plan.Count + 2 + lowerBound;
                    var estimatedVolume = plan.Sum(item => item.Box.Volume) + first.Attempt.Box.Volume + second.Box.Volume + lowerBound * minimumVolume;
                    var difficulty = RemainingDifficulty(afterSecond, sourceBoxes);
                    var candidate = new ScoredStep(first.Attempt, estimatedCount, estimatedVolume, difficulty, afterSecond.Sum(item => item.Volume));
                    if (bestContinuation is null || candidate.CompareTo(bestContinuation) < 0) bestContinuation = candidate;
                }
                if (bestContinuation is not null) scored.Add(bestContinuation);
            }

            var selected = scored.Order().FirstOrDefault()?.Attempt;
            if (selected is null) return null;
            plan.Add(selected);
            remaining = RemovePacked(remaining, selected);
        }

        return plan;
    }

    internal static IReadOnlyList<BoxType> FilterCandidateBoxes(IReadOnlyList<BoxType> boxes, IReadOnlyList<PackingItemUnit> remaining)
    {
        var feasible = boxes.Where(box => remaining.Any(item => CanEverFit(item, box)))
            .OrderBy(box => box.Volume).ThenBy(box => box.Cost ?? decimal.MaxValue).ThenBy(box => box.Id)
            .ToList();
        var result = new List<BoxType>(feasible.Count);
        foreach (var box in feasible)
        {
            var equivalentDominated = result.Any(other =>
                SameInterior(other, box) && WeightCapacity(other) >= WeightCapacity(box) &&
                (other.Cost ?? decimal.MaxValue) <= (box.Cost ?? decimal.MaxValue));
            if (!equivalentDominated) result.Add(box);
        }
        return result;
    }

    private PackingAttempt Evaluate(BoxType box, IReadOnlyList<PackingItemUnit> remaining, Dictionary<string, PackingAttempt> cache)
    {
        var key = CacheKey(box, remaining);
        if (cache.TryGetValue(key, out var result)) return result;
        result = algorithm.Pack(box, remaining);
        cache[key] = result;
        return result;
    }

    private static int RemainingBoxLowerBound(IReadOnlyList<PackingItemUnit> remaining, IReadOnlyList<BoxType> boxes)
    {
        if (remaining.Count == 0) return 0;
        var maxVolume = boxes.Max(box => box.Volume);
        var volumeBound = (int)Math.Ceiling(remaining.Sum(item => item.Volume) / (double)maxVolume);
        var maxWeight = boxes.Max(WeightCapacity);
        var weightBound = double.IsPositiveInfinity(maxWeight) ? 0 : (int)Math.Ceiling(remaining.Sum(item => item.WeightKg) / maxWeight);
        return Math.Max(1, Math.Max(volumeBound, weightBound));
    }

    private static long MinimumFeasibleBoxVolume(IReadOnlyList<PackingItemUnit> remaining, IReadOnlyList<BoxType> boxes)
    {
        if (remaining.Count == 0) return 0;
        return boxes.Where(box => remaining.Any(item => CanEverFit(item, box))).Select(box => box.Volume).DefaultIfEmpty(boxes.Min(box => box.Volume)).Min();
    }

    private static double RemainingDifficulty(IReadOnlyList<PackingItemUnit> remaining, IReadOnlyList<BoxType> boxes)
    {
        double score = 0;
        foreach (var item in remaining)
        {
            var fitCount = boxes.Count(box => CanEverFit(item, box));
            score += 1d / Math.Max(1, fitCount) + (item.AllowRotation ? 0 : 1);
        }
        return score;
    }

    private static List<PackingItemUnit> RemovePacked(IReadOnlyList<PackingItemUnit> source, PackingAttempt attempt)
    {
        var ids = attempt.PackedItems.Select(item => item.InstanceId).ToHashSet();
        return source.Where(item => !ids.Contains(item.InstanceId)).ToList();
    }

    private static string CacheKey(BoxType box, IReadOnlyList<PackingItemUnit> remaining)
    {
        var builder = new StringBuilder(box.Id.ToString("N"));
        foreach (var item in remaining.OrderBy(item => item.InstanceId)) builder.Append('|').Append(item.InstanceId.ToString("N"));
        return builder.ToString();
    }

    private static bool CanEverFit(PackingItemUnit item, BoxType box)
    {
        if (box.MaxWeightKg is { } capacity && item.WeightKg > capacity + 1e-9) return false;
        if (!item.AllowRotation) return item.Length <= box.Length && item.Width <= box.Width && item.Height <= box.Height;
        Span<int> itemDimensions = stackalloc[] { item.Length, item.Width, item.Height };
        Span<int> boxDimensions = stackalloc[] { box.Length, box.Width, box.Height };
        itemDimensions.Sort(); boxDimensions.Sort();
        return itemDimensions[0] <= boxDimensions[0] && itemDimensions[1] <= boxDimensions[1] && itemDimensions[2] <= boxDimensions[2];
    }

    private static bool SameInterior(BoxType first, BoxType second)
    {
        Span<int> a = stackalloc[] { first.Length, first.Width, first.Height };
        Span<int> b = stackalloc[] { second.Length, second.Width, second.Height };
        a.Sort(); b.Sort();
        return a.SequenceEqual(b);
    }

    private static double WeightCapacity(BoxType box) => box.MaxWeightKg ?? double.PositiveInfinity;

    private sealed record FirstStep(PackingAttempt Attempt, List<PackingItemUnit> Remaining);
    private sealed record ScoredStep(PackingAttempt Attempt, int EstimatedBoxCount, long EstimatedTotalVolume, double RemainingDifficulty, long RemainingVolume) : IComparable<ScoredStep>
    {
        public int CompareTo(ScoredStep? other)
        {
            if (other is null) return -1;
            var value = EstimatedBoxCount.CompareTo(other.EstimatedBoxCount); if (value != 0) return value;
            value = EstimatedTotalVolume.CompareTo(other.EstimatedTotalVolume); if (value != 0) return value;
            value = RemainingDifficulty.CompareTo(other.RemainingDifficulty); if (value != 0) return value;
            value = RemainingVolume.CompareTo(other.RemainingVolume); if (value != 0) return value;
            value = other.Attempt.PackedVolume.CompareTo(Attempt.PackedVolume); if (value != 0) return value;
            value = Attempt.Box.Volume.CompareTo(other.Attempt.Box.Volume); return value != 0 ? value : Attempt.Box.Id.CompareTo(other.Attempt.Box.Id);
        }
    }
}
