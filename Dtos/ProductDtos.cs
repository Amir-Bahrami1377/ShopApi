using System.ComponentModel.DataAnnotations;

namespace ProductOrderApi.Dtos;

public sealed record CreateProductRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal Price { get; init; }

    [Range(0, int.MaxValue)]
    public int Stock { get; init; }
}

public sealed record ProductResponse(Guid Id, string Name, decimal Price, int Stock);
