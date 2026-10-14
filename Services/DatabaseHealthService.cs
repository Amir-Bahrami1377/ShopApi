using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Data;
using ProductOrderApi.Errors;

namespace ProductOrderApi.Services;

public sealed class DatabaseHealthService(AppDbContext dbContext)
{
    public async Task EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        Exception? connectionException = null;
        try
        {
            if (await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            connectionException = exception;
        }

        throw AppException.ServiceUnavailable("The database is unavailable.", connectionException);
    }
}
