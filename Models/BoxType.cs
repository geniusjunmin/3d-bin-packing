using System.ComponentModel.DataAnnotations;

namespace BinPacking.Web.Models;

public sealed record BoxType
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
    public double? MaxWeightKg { get; init; }

    [Range(0, 1_000_000)]
    public decimal? Cost { get; init; }

    public long Volume => (long)Length * Width * Height;
}

public sealed record BoxInput
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
    public double? MaxWeightKg { get; init; }

    [Range(0, 1_000_000)]
    public decimal? Cost { get; init; }
}
