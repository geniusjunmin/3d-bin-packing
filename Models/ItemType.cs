using System.ComponentModel.DataAnnotations;

namespace BinPacking.Web.Models;

public sealed record ItemType
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [Required, StringLength(80)]
    public required string Name { get; init; }

    [Range(1, 100_000)]
    public int Length { get; init; }

    [Range(1, 100_000)]
    public int Width { get; init; }

    [Range(1, 100_000)]
    public int Height { get; init; }

    [Range(0.001, 1_000_000)]
    public double? WeightKg { get; init; }

    [Range(0, 10_000)]
    public int Quantity { get; init; } = 1;

    public bool AllowRotation { get; init; } = true;

    public string Color { get; init; } = "#60A5FA";

    public long Volume => (long)Length * Width * Height;
}

public sealed record ItemInput
{
    [Required, StringLength(80)]
    public required string Name { get; init; }

    [Range(1, 100_000)]
    public int Length { get; init; }

    [Range(1, 100_000)]
    public int Width { get; init; }

    [Range(1, 100_000)]
    public int Height { get; init; }

    [Range(0.001, 1_000_000)]
    public double? WeightKg { get; init; }

    [Range(0, 10_000)]
    public int Quantity { get; init; } = 1;

    public bool AllowRotation { get; init; } = true;

    [RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "颜色必须是 #RRGGBB 格式。")]
    public string? Color { get; init; }
}
