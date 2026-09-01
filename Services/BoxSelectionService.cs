using BinPacking.Web.Algorithms;
using BinPacking.Web.Models;

namespace BinPacking.Web.Services;

public sealed class BoxSelectionService
{
    private readonly IPackingAlgorithm algorithm;
    private readonly CatalogStore store;
    private readonly BoxPlanOptimizer planOptimizer;

    public BoxSelectionService(IPackingAlgorithm algorithm, CatalogStore store)
    {
        this.algorithm = algorithm;
        this.store = store;
        planOptimizer = new BoxPlanOptimizer(algorithm);
    }

    public PackingResult Pack(PackOrderRequest request)
    {
        var itemTypes = store.GetItems().ToDictionary(item => item.Id);
        var boxes = store.GetBoxes();
        var units = new List<PackingItemUnit>();

        foreach (var line in request.Items.Where(line => line.Quantity > 0))
        {
            if (!itemTypes.TryGetValue(line.ItemId, out var item))
                return Failure($"商品 {line.ItemId} 不存在。", []);

            for (var sequence = 1; sequence <= line.Quantity; sequence++)
            {
                units.Add(new PackingItemUnit(
                    Guid.NewGuid(), item.Id, item.Name, sequence,
                    item.Length, item.Width, item.Height,
                    item.WeightKg ?? 0, item.AllowRotation, item.Color));
            }
        }

        if (units.Count == 0) return Failure("订单中没有可装箱的商品。", []);
        if (boxes.Count == 0) return Failure("请先添加至少一种箱型。", units.Select(UnitName).ToArray());

        var impossible = units.Where(unit => !boxes.Any(box => CanEverFit(unit, box))).ToList();
        if (impossible.Count > 0)
            return Failure($"有 {impossible.Count} 件商品无法放入任何现有箱型。", impossible.Select(UnitName).ToArray());

        var plans = boxes
            .Select(box => BuildHomogeneousPlan(box, units))
            .Where(plan => plan is not null)
            .Cast<List<PackingAttempt>>()
            .ToList();

        var mixed = planOptimizer.BuildPlan(boxes, units);
        if (mixed is not null) plans.Add(mixed);

        var best = plans
            .OrderBy(plan => plan.Count)
            .ThenBy(plan => plan.Sum(attempt => attempt.Box.Volume))
            .ThenByDescending(Utilization)
            .ThenBy(plan => plan.Sum(attempt => attempt.Box.Cost ?? 0))
            .FirstOrDefault();

        if (best is null)
            return Failure("未能生成有效装箱方案，请检查箱型承重和商品尺寸。", units.Select(UnitName).ToArray());

        return Success(best, units);
    }

    private List<PackingAttempt>? BuildHomogeneousPlan(BoxType box, IReadOnlyList<PackingItemUnit> source)
    {
        var remaining = source.ToList();
        var plan = new List<PackingAttempt>();

        while (remaining.Count > 0)
        {
            var attempt = algorithm.Pack(box, remaining);
            if (attempt.PackedItems.Count == 0) return null;
            plan.Add(attempt);
            var packedIds = attempt.PackedItems.Select(item => item.InstanceId).ToHashSet();
            remaining.RemoveAll(item => packedIds.Contains(item.InstanceId));
        }

        return plan;
    }

    private static bool CanEverFit(PackingItemUnit unit, BoxType box)
    {
        if (box.MaxWeightKg is { } capacity && unit.WeightKg > capacity + 1e-9) return false;
        var dimensions = new[] { unit.Length, unit.Width, unit.Height };
        var boxDimensions = new[] { box.Length, box.Width, box.Height };
        if (!unit.AllowRotation)
            return dimensions.Zip(boxDimensions).All(pair => pair.First <= pair.Second);

        Array.Sort(dimensions);
        Array.Sort(boxDimensions);
        return dimensions.Zip(boxDimensions).All(pair => pair.First <= pair.Second);
    }

    private static double Utilization(IReadOnlyList<PackingAttempt> plan)
    {
        var boxVolume = plan.Sum(attempt => attempt.Box.Volume);
        return boxVolume == 0 ? 0 : plan.Sum(attempt => attempt.PackedVolume) / (double)boxVolume;
    }

    private static PackingResult Success(IReadOnlyList<PackingAttempt> plan, IReadOnlyList<PackingItemUnit> units)
    {
        var packedBoxes = plan.Select((attempt, index) => new PackedBox
        {
            Number = index + 1,
            Box = attempt.Box,
            Items = attempt.PackedItems,
            TotalWeightKg = Math.Round(attempt.PackedWeightKg, 3),
            UtilizationPercent = Math.Round(attempt.PackedVolume / (double)attempt.Box.Volume * 100, 2)
        }).ToArray();

        var totalItemVolume = units.Sum(item => item.Volume);
        var totalBoxVolume = packedBoxes.Sum(item => item.Box.Volume);
        return new PackingResult
        {
            Success = true,
            Summary = new PackingSummary
            {
                TotalItemCount = units.Count,
                TotalBoxCount = packedBoxes.Length,
                TotalItemVolumeMm3 = totalItemVolume,
                TotalBoxVolumeMm3 = totalBoxVolume,
                UtilizationPercent = Math.Round(totalItemVolume / (double)totalBoxVolume * 100, 2),
                TotalCost = packedBoxes.Sum(item => item.Box.Cost ?? 0),
                BoxesByType = packedBoxes
                    .GroupBy(item => item.Box.Name)
                    .ToDictionary(group => group.Key, group => group.Count())
            },
            Boxes = packedBoxes,
            UnpackedItems = []
        };
    }

    private static PackingResult Failure(string error, IReadOnlyList<string> unpacked) => new()
    {
        Success = false,
        Error = error,
        Summary = new PackingSummary
        {
            BoxesByType = new Dictionary<string, int>()
        },
        Boxes = [],
        UnpackedItems = unpacked
    };

    private static string UnitName(PackingItemUnit item) => $"{item.Name} #{item.Sequence}";
}
