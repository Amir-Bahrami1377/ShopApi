using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Data;
using ProductOrderApi.Domain;
using ProductOrderApi.Dtos;
using ProductOrderApi.Errors;

namespace ProductOrderApi.Services;

public sealed class ProductService(AppDbContext dbContext) : IProductService
{
    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["name"] = ["Name must not be empty or whitespace."]
            });
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Price = request.Price,
            Stock = request.Stock
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(product);
    }

    public async Task<PagedResult<ProductResponse>> GetAllAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                [page < 1 ? "page" : "pageSize"] =
                    [page < 1 ? "Page must be at least 1." : "Page size must be between 1 and 100."]
            });
        }

        var skip = (long)(page - 1) * pageSize;
        if (skip > int.MaxValue)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["page"] = ["Page is too large for the selected page size."]
            });
        }

        var query = dbContext.Products.AsNoTracking().OrderBy(product => product.Name).ThenBy(product => product.Id);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((int)skip)
            .Take(pageSize)
            .Select(product => new ProductResponse(product.Id, product.Name, product.Price, product.Stock))
            .ToListAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedResult<ProductResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<ProductResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new ProductResponse(candidate.Id, candidate.Name, candidate.Price, candidate.Stock))
            .SingleOrDefaultAsync(cancellationToken);

        return product ?? throw AppException.NotFound($"Product '{id}' was not found.");
    }

    private static ProductResponse Map(Product product) =>
        new(product.Id, product.Name, product.Price, product.Stock);
}
