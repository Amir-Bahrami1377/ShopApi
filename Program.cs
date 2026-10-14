using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProductOrderApi.Data;
using ProductOrderApi.Errors;
using ProductOrderApi.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Logging.AddDebug();

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => ToCamelCasePath(entry.Key),
                    entry => entry.Value!.Errors
                        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                            ? "The supplied value is invalid."
                            : error.ErrorMessage)
                        .ToArray());
            var problem = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Detail = "One or more validation errors occurred.",
                Instance = context.HttpContext.Request.Path
            };
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

            var result = new BadRequestObjectResult(problem);
            result.ContentTypes.Add("application/problem+json");
            return result;
        };
    });

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions.TryAdd(
            "traceId",
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    options.UseSqlServer(connectionString);
});

builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<DatabaseHealthService>();
builder.Services.AddOptions<PaymentOptions>()
    .Bind(builder.Configuration.GetSection(PaymentOptions.SectionName))
    .Validate(
        options => options.TimeoutSeconds is > 0 and <= 3600,
        "Payment:TimeoutSeconds must be between 1 and 3600.")
    .Validate(
        options => options.ArtificialDelayMilliseconds >= 0,
        "Payment:ArtificialDelayMilliseconds must not be negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<IPaymentService, FakePaymentService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
        options
            .WithTitle("Product and Order API")
            .DisableAgent());
}

var migrateOnStartup = app.Configuration.GetValue("Database:MigrateOnStartup", true);
var seedOnStartup = app.Configuration.GetValue("Database:SeedOnStartup", true);
if (migrateOnStartup || seedOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (migrateOnStartup)
    {
        await dbContext.Database.MigrateAsync();
    }

    if (seedOnStartup)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");
        await DatabaseSeeder.SeedAsync(dbContext, logger);
    }
}

await app.RunAsync();

static string ToCamelCasePath(string key)
{
    if (string.IsNullOrEmpty(key))
    {
        return key;
    }

    return char.ToLowerInvariant(key[0]) + key[1..];
}

public partial class Program;
