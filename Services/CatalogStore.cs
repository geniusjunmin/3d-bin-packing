using BinPacking.Web.Models;

namespace BinPacking.Web.Services;

public sealed class CatalogStore
{
    private readonly object _gate = new();
    private readonly List<BoxType> _boxes;
    private readonly List<ItemType> _items;

    public CatalogStore()
    {
        _boxes =
        [
            new BoxType { Name = "Small Box", Length = 300, Width = 200, Height = 150, MaxWeightKg = 12, Cost = 2.50m },
            new BoxType { Name = "Medium Box", Length = 500, Width = 400, Height = 300, MaxWeightKg = 30, Cost = 5.80m },
            new BoxType { Name = "Large Box", Length = 800, Width = 600, Height = 500, MaxWeightKg = 80, Cost = 12.00m }
        ];

        _items =
        [
            new ItemType { Name = "Coffee Maker", Length = 260, Width = 180, Height = 220, WeightKg = 3.2, Quantity = 1, AllowRotation = true },
            new ItemType { Name = "Book Set", Length = 210, Width = 150, Height = 80, WeightKg = 1.4, Quantity = 2, AllowRotation = true },
            new ItemType { Name = "Storage Jar", Length = 120, Width = 120, Height = 190, WeightKg = 0.8, Quantity = 2, AllowRotation = false },
            new ItemType { Name = "Desk Lamp", Length = 320, Width = 140, Height = 110, WeightKg = 1.1, Quantity = 1, AllowRotation = true },
            new ItemType { Name = "Headphones", Length = 190, Width = 170, Height = 90, WeightKg = 0.4, Quantity = 3, AllowRotation = true }
        ];
    }

    public IReadOnlyList<BoxType> GetBoxes()
    {
        lock (_gate) return _boxes.OrderBy(box => box.Volume).ToArray();
    }

    public BoxType? GetBox(Guid id)
    {
        lock (_gate) return _boxes.FirstOrDefault(box => box.Id == id);
    }

    public BoxType AddBox(BoxInput input)
    {
        var box = ToBox(input, Guid.NewGuid());
        lock (_gate) _boxes.Add(box);
        return box;
    }

    public BoxType? UpdateBox(Guid id, BoxInput input)
    {
        lock (_gate)
        {
            var index = _boxes.FindIndex(box => box.Id == id);
            if (index < 0) return null;
            var box = ToBox(input, id);
            _boxes[index] = box;
            return box;
        }
    }

    public bool DeleteBox(Guid id)
    {
        lock (_gate) return _boxes.RemoveAll(box => box.Id == id) > 0;
    }

    public IReadOnlyList<ItemType> GetItems()
    {
        lock (_gate) return _items.OrderBy(item => item.Name).ToArray();
    }

    public ItemType? GetItem(Guid id)
    {
        lock (_gate) return _items.FirstOrDefault(item => item.Id == id);
    }

    public ItemType AddItem(ItemInput input)
    {
        var item = ToItem(input, Guid.NewGuid());
        lock (_gate) _items.Add(item);
        return item;
    }

    public ItemType? UpdateItem(Guid id, ItemInput input)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            var item = ToItem(input, id);
            _items[index] = item;
            return item;
        }
    }

    public bool DeleteItem(Guid id)
    {
        lock (_gate) return _items.RemoveAll(item => item.Id == id) > 0;
    }

    private static BoxType ToBox(BoxInput input, Guid id) => new()
    {
        Id = id,
        Name = input.Name.Trim(),
        Length = input.Length,
        Width = input.Width,
        Height = input.Height,
        MaxWeightKg = input.MaxWeightKg,
        Cost = input.Cost
    };

    private static ItemType ToItem(ItemInput input, Guid id) => new()
    {
        Id = id,
        Name = input.Name.Trim(),
        Length = input.Length,
        Width = input.Width,
        Height = input.Height,
        WeightKg = input.WeightKg,
        Quantity = input.Quantity,
        AllowRotation = input.AllowRotation
    };
}
