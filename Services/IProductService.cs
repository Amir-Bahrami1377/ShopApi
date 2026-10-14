using ProductOrderApi.Dtos;

namespace ProductOrderApi.Services;

public interface IProductService
{
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);
    Task<PagedResult<ProductResponse>> GetAllAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<ProductResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
