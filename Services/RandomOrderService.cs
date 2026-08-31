using BinPacking.Web.Models;

namespace BinPacking.Web.Services;

public sealed class RandomOrderService(CatalogStore store, BoxSelectionService selectionService)
{
    public RandomOrderResponse GenerateAndPack()
    {
        var items = store.GetItems();
        if (items.Count == 0)
        {
            var emptyRequest = new PackOrderRequest { Items = [] };
            return new RandomOrderResponse([], selectionService.Pack(emptyRequest));
        }

        var count = Random.Shared.Next(1, Math.Min(items.Count, 5) + 1);
        var lines = items
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .Select(item => new OrderLineRequest
            {
                ItemId = item.Id,
                Quantity = Random.Shared.Next(1, Math.Min(Math.Max(item.Quantity, 2), 6) + 1)
            })
            .ToArray();

        var request = new PackOrderRequest { Items = lines };
        return new RandomOrderResponse(lines, selectionService.Pack(request));
    }
}
