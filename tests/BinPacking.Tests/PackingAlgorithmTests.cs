using BinPacking.Web.Algorithms;
using BinPacking.Web.Models;
using BinPacking.Web.Services;

namespace BinPacking.Tests;

public sealed class PackingAlgorithmTests
{
    private readonly ExtremePointPackingAlgorithm _algorithm = new();

    [Fact]
    public void Pack_RotatesItem_WhenOriginalOrientationDoesNotFit()
    {
        var box = Box("Rotation Box", 100, 80, 60);
        var item = Unit("Tall Item", 80, 60, 100, allowRotation: true);

        var result = _algorithm.Pack(box, [item]);

        var packed = Assert.Single(result.PackedItems);
        Assert.Empty(result.UnpackedItems);
        Assert.NotEqual("L-W-H", packed.Rotation);
        Assert.InRange(packed.X + packed.Length, 0, box.Length);
        Assert.InRange(packed.Y + packed.Width, 0, box.Width);
        Assert.InRange(packed.Z + packed.Height, 0, box.Height);
    }

    [Fact]
    public void Pack_ProducesOnlyInBoundsNonOverlappingPlacements()
    {
        var box = Box("Grid Box", 300, 200, 200);
        var items = Enumerable.Range(1, 12)
            .Select(index => Unit($"Cube {index}", 100, 100, 100, sequence: index))
            .ToArray();

        var result = _algorithm.Pack(box, items);

        Assert.Equal(12, result.PackedItems.Count);
        Assert.Empty(result.UnpackedItems);
        AssertValid(box, result.PackedItems);
    }

    [Fact]
    public void Pack_RespectsWeightCapacity()
    {
        var box = Box("Weight Box", 500, 500, 500, maxWeight: 5);
        var items = new[]
        {
            Unit("Heavy 1", 100, 100, 100, weight: 3, sequence: 1),
            Unit("Heavy 2", 100, 100, 100, weight: 3, sequence: 2)
        };

        var result = _algorithm.Pack(box, items);

        Assert.Single(result.PackedItems);
        Assert.Single(result.UnpackedItems);
        Assert.True(result.PackedWeightKg <= 5);
    }

    [Fact]
    public void Pack_RejectsPlacement_WithOnlyTinyBottomSupport()
    {
        var box = Box("Stability Box", 100, 100, 100);
        var pillar = Unit("Pillar", 20, 20, 90, allowRotation: false);
        var plate = Unit("Wide Plate", 100, 100, 2, allowRotation: false);

        var result = _algorithm.Pack(box, [pillar, plate]);

        Assert.Contains(result.PackedItems, item => item.Name == "Pillar");
        Assert.Contains(result.UnpackedItems, item => item.Name == "Wide Plate");
    }

    [Fact]
    public void Pack_AllowsPlacement_FullySupportedByMultipleItems()
    {
        var box = Box("Bridge Box", 100, 100, 50);
        var left = Unit("Left Support", 50, 100, 30, allowRotation: false);
        var right = Unit("Right Support", 50, 100, 30, allowRotation: false);
        var top = Unit("Top Plate", 100, 100, 10, allowRotation: false);

        var result = _algorithm.Pack(box, [left, right, top]);

        Assert.Empty(result.UnpackedItems);
        var packedTop = Assert.Single(result.PackedItems, item => item.Name == "Top Plate");
        Assert.Equal(30, packedTop.Z);
        Assert.Equal(100, packedTop.SupportPercent);
        AssertValid(box, result.PackedItems);
    }

    [Fact]
    public void BoxSelection_SplitsOrderAcrossMultipleBoxes()
    {
        var store = EmptyStore();
        var box = store.AddBox(new BoxInput { Name = "Only Box", Length = 100, Width = 100, Height = 100 });
        var item = store.AddItem(new ItemInput { Name = "Large Cube", Length = 80, Width = 80, Height = 80, Quantity = 3, AllowRotation = true });
        var service = new BoxSelectionService(_algorithm, store);

        var result = service.Pack(new PackOrderRequest
        {
            Items = [new OrderLineRequest { ItemId = item.Id, Quantity = 3 }]
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Summary.TotalBoxCount);
        Assert.All(result.Boxes, packedBox =>
        {
            Assert.Single(packedBox.Items);
            AssertValid(box, packedBox.Items);
        });
    }

    [Fact]
    public void BoxSelection_UsesSmallestBox_WhenBoxCountIsEqual()
    {
        var store = EmptyStore();
        var small = store.AddBox(new BoxInput { Name = "Small", Length = 100, Width = 100, Height = 100 });
        store.AddBox(new BoxInput { Name = "Large", Length = 200, Width = 200, Height = 200 });
        var item = store.AddItem(new ItemInput { Name = "Cube", Length = 50, Width = 50, Height = 50, Quantity = 1, AllowRotation = true });
        var service = new BoxSelectionService(_algorithm, store);

        var result = service.Pack(new PackOrderRequest
        {
            Items = [new OrderLineRequest { ItemId = item.Id, Quantity = 1 }]
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(small.Id, Assert.Single(result.Boxes).Box.Id);
    }

    [Fact]
    public void BoxSelection_PreservesConfiguredBoxAndItemColors()
    {
        var store = EmptyStore();
        store.AddBox(new BoxInput { Name = "Blue Box", Length = 100, Width = 100, Height = 100, Color = "#123ABC" });
        var item = store.AddItem(new ItemInput { Name = "Pink Cube", Length = 50, Width = 50, Height = 50, Quantity = 1, AllowRotation = true, Color = "#F06292" });
        var service = new BoxSelectionService(_algorithm, store);

        var result = service.Pack(new PackOrderRequest
        {
            Items = [new OrderLineRequest { ItemId = item.Id, Quantity = 1 }]
        });

        var packedBox = Assert.Single(result.Boxes);
        Assert.Equal("#123ABC", packedBox.Box.Color);
        Assert.Equal("#F06292", Assert.Single(packedBox.Items).Color);
    }

    private static CatalogStore EmptyStore()
    {
        var store = new CatalogStore();
        foreach (var box in store.GetBoxes()) store.DeleteBox(box.Id);
        foreach (var item in store.GetItems()) store.DeleteItem(item.Id);
        return store;
    }

    private static BoxType Box(string name, int length, int width, int height, double? maxWeight = null) => new()
    {
        Name = name,
        Length = length,
        Width = width,
        Height = height,
        MaxWeightKg = maxWeight
    };

    private static PackingItemUnit Unit(
        string name,
        int length,
        int width,
        int height,
        bool allowRotation = true,
        double weight = 0,
        int sequence = 1) => new(
            Guid.NewGuid(), Guid.NewGuid(), name, sequence,
            length, width, height, weight, allowRotation, "#60A5FA");

    private static void AssertValid(BoxType box, IReadOnlyList<PackedItem> items)
    {
        foreach (var item in items)
        {
            Assert.True(item.X >= 0 && item.Y >= 0 && item.Z >= 0);
            Assert.True(item.X + item.Length <= box.Length);
            Assert.True(item.Y + item.Width <= box.Width);
            Assert.True(item.Z + item.Height <= box.Height);
            Assert.InRange(item.SupportPercent, 90, 100);
        }

        for (var first = 0; first < items.Count; first++)
        for (var second = first + 1; second < items.Count; second++)
        {
            var a = items[first];
            var b = items[second];
            var overlap =
                a.X < b.X + b.Length && a.X + a.Length > b.X &&
                a.Y < b.Y + b.Width && a.Y + a.Width > b.Y &&
                a.Z < b.Z + b.Height && a.Z + a.Height > b.Z;
            Assert.False(overlap, $"{a.Name} overlaps {b.Name}");
        }
    }
}
