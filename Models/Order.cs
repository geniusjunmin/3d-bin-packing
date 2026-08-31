using System.ComponentModel.DataAnnotations;

namespace BinPacking.Web.Models;

public sealed record OrderLineRequest
{
    public Guid ItemId { get; init; }

    [Range(1, 10_000)]
    public int Quantity { get; init; }
}

public sealed record PackOrderRequest
{
    [Required, MinLength(1)]
    public required IReadOnlyList<OrderLineRequest> Items { get; init; }
}

public sealed record RandomOrderResponse(
    IReadOnlyList<OrderLineRequest> OrderLines,
    PackingResult Result);
