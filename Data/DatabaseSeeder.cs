using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Domain;

namespace ProductOrderApi.Data;

public static class SeedProductIds
{
    public static readonly Guid ConcurrencyWidget = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid StandardWidget = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PremiumWidget = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid OutOfStockWidget = Guid.Parse("44444444-4444-4444-4444-444444444444");
}

public static class DatabaseSeeder
{
    private static readonly Product[] DemoProducts =
    [
        new() { Id = SeedProductIds.ConcurrencyWidget, Name = "Concurrency Widget", Price = 9.99m, Stock = 1 },
        new() { Id = SeedProductIds.StandardWidget, Name = "Standard Widget", Price = 19.99m, Stock = 100 },
        new() { Id = SeedProductIds.PremiumWidget, Name = "Premium Widget", Price = 49.99m, Stock = 25 },
        new() { Id = SeedProductIds.OutOfStockWidget, Name = "Out of Stock Widget", Price = 5.00m, Stock = 0 }
    ];

    public static async Task SeedAsync(AppDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        var seedIds = DemoProducts.Select(product => product.Id).ToArray();
        var existingIds = await dbContext.Products
            .Where(product => seedIds.Contains(product.Id))
            .Select(product => product.Id)
            .ToListAsync(cancellationToken);
        var existingSet = existingIds.ToHashSet();
        var missing = DemoProducts
            .Where(product => !existingSet.Contains(product.Id))
            .Select(product => new Product
            {
                Id = product.Id,
                Name = product.Name,
                Price = product.Price,
                Stock = product.Stock
            })
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        dbContext.Products.AddRange(missing);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Inserted {ProductCount} demo products.", missing.Length);
    }
}
