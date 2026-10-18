using AdPlay.Api.Common;
using AdPlay.Api.Common.Querying;
using AdPlay.Api.Data;
using AdPlay.Api.Features.Payments;
using AdPlay.Api.Features.Products;
using AdPlay.Api.Features.Subscriptions;
using AdPlay.Api.Middleware;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

// Connection pool size and timeouts are set in the connection string.
builder.Services.AddDbContextPool<AppDbContext>(o => o
    .UseMySQL(connectionString, my => my.CommandTimeout(30))
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDbErrorClassifier, MySqlDbErrorClassifier>();
builder.Services.AddScoped(typeof(IReadRepository<>), typeof(EfReadRepository<>));
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<ProductSearchService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddSingleton<IPaymentProviderClient, FakePaymentProviderClient>();
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.Configure<RequestLoggingOptions>(builder.Configuration.GetSection("RequestLogging"));
if (builder.Configuration.GetValue("Payments:EnableReconciliation", true))
    builder.Services.AddHostedService<PaymentReconciliationService>();

builder.Services.AddControllers(o => o.Filters.Add<InvalidSortExceptionFilter>());
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOutputCache(o =>
    o.AddPolicy("ProductSearch", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*").Tag("products")));

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    if (app.Configuration.GetValue<bool>("Database:SeedOnStartup"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await DatabaseSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

app.UseHttpsRedirection();
app.UseOutputCache();
app.MapControllers();

app.Run();
