using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using ProductOrderApi.Data;
using ProductOrderApi.Services;

namespace ProductOrderApi.IntegrationTests;

internal sealed class IntegrationTestHost : IAsyncDisposable
{
    private const string DefaultConnectionString =
        "Server=localhost,1433;Database=ProductOrderApiTests;User Id=sa;" +
        "Password=YourStrong!Passw0rd;Encrypt=False;TrustServerCertificate=True;" +
        "MultipleActiveResultSets=True";

    private readonly ProductOrderApiFactory _factory;
    private bool _databaseInitialized;

    private IntegrationTestHost(
        ProductOrderApiFactory factory,
        HttpClient client)
    {
        _factory = factory;
        Client = client;
    }

    public HttpClient Client { get; }

    public ControllablePaymentService Payment => _factory.Payment;

    public async Task<int> CountOrdersForIdempotencyKeyAsync(string idempotencyKey)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Orders.CountAsync(
            order => order.IdempotencyKey == idempotencyKey);
    }

    public async Task SeedDatabaseAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("IntegrationTestSeeder");
        await DatabaseSeeder.SeedAsync(dbContext, logger);
    }

    public async Task<int> CountProductsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Products.CountAsync();
    }

    public static async Task<IntegrationTestHost> StartAsync()
    {
        var factory = new ProductOrderApiFactory(BuildIsolatedConnectionString());
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var host = new IntegrationTestHost(factory, client);

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.MigrateAsync();
                host._databaseInitialized = true;
            }

            using var healthResponse = await client.GetAsync("/health");
            healthResponse.EnsureSuccessStatusCode();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_databaseInitialized)
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.EnsureDeletedAsync(CancellationToken.None);
            }
        }
        finally
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }

    private static string BuildIsolatedConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("TEST_DB");
        var builder = new SqlConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured);
        var databasePrefix = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? "ProductOrderApiTests"
            : builder.InitialCatalog;
        databasePrefix = databasePrefix[..Math.Min(databasePrefix.Length, 95)];
        builder.InitialCatalog = $"{databasePrefix}_{Guid.NewGuid():N}";

        return builder.ConnectionString;
    }

    private sealed class ProductOrderApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        public ControllablePaymentService Payment { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                    ["Database:MigrateOnStartup"] = "false",
                    ["Database:SeedOnStartup"] = "false",
                    ["Payment:TimeoutSeconds"] = "1"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPaymentService>();
                services.AddSingleton<IPaymentService>(Payment);
            });
        }
    }
}
