using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Data;
using ProductOrderApi.Errors;

namespace ProductOrderApi.Services;

public sealed class DatabaseHealthService(
    AppDbContext dbContext,
    ILogger<DatabaseHealthService> logger)
{
    public async Task EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return;
            }

            logger.LogWarning("Database connectivity health check failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database connectivity health check failed.");
        }

        throw AppException.ServiceUnavailable("The database is unavailable.");
    }
}
