using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy.WithOrigins("http://localhost:5000").AllowAnyHeader().AllowAnyMethod());
});

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorefrontPoC", "Catalog.Api", "catalog.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<CatalogDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
    if (feature?.Error is not null)
    {
        logger.LogError(feature.Error, "Unhandled exception while processing request");
    }

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Problem(title: "Unexpected error", detail: "Catalog.Api could not process the request.", statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
}));

app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("Frontend");
}

app.MapHealthChecks("/healthz");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    db.Database.EnsureCreated();
    CatalogSeed.Seed(db);
}

app.MapGet("/api/products", async (string? category, CatalogDbContext db) =>
{
    var query = db.Products.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(category))
    {
        query = query.Where(product => product.Category == category);
    }

    var products = await query.OrderBy(product => product.Category).ThenBy(product => product.Name).Select(product => product.ToDto()).ToListAsync();
    return Results.Ok(products);
});

app.MapGet("/api/products/{id:guid}", async (Guid id, CatalogDbContext db) =>
{
    var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(product => product.Id == id);
    return product is null ? Results.NotFound() : Results.Ok(product.ToDto());
});

app.MapPost("/api/stock/reserve", async (StockReservationRequest request, CatalogDbContext db) =>
{
    var validation = ValidateItems(request.Items);
    if (validation is not null)
    {
        return validation;
    }

    await using var transaction = await db.Database.BeginTransactionAsync();
    var requested = request.Items.GroupBy(item => item.ProductId).Select(group => new CartItemDto(group.Key, group.Sum(item => item.Quantity))).ToList();
    var ids = requested.Select(item => item.ProductId).ToList();
    var products = await db.Products.Where(product => ids.Contains(product.Id)).ToListAsync();

    var shortages = requested
        .Select(item =>
        {
            var product = products.SingleOrDefault(candidate => candidate.Id == item.ProductId);
            return product is null || product.StockQuantity < item.Quantity
                ? new StockShortageDto(item.ProductId, product?.Name ?? "Unknown product", item.Quantity, product?.StockQuantity ?? 0)
                : null;
        })
        .Where(shortage => shortage is not null)
        .Cast<StockShortageDto>()
        .ToList();

    if (shortages.Count > 0)
    {
        return Results.Problem(
            title: "Insufficient stock",
            detail: "One or more products do not have enough stock.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["shortages"] = shortages });
    }

    foreach (var item in requested)
    {
        products.Single(product => product.Id == item.ProductId).StockQuantity -= item.Quantity;
    }

    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    return Results.Ok();
});

app.MapPost("/api/stock/release", async (StockReleaseRequest request, CatalogDbContext db) =>
{
    var validation = ValidateItems(request.Items);
    if (validation is not null)
    {
        return validation;
    }

    var requested = request.Items.GroupBy(item => item.ProductId).Select(group => new CartItemDto(group.Key, group.Sum(item => item.Quantity))).ToList();
    var ids = requested.Select(item => item.ProductId).ToList();
    var products = await db.Products.Where(product => ids.Contains(product.Id)).ToListAsync();

    foreach (var item in requested)
    {
        var product = products.SingleOrDefault(product => product.Id == item.ProductId);
        if (product is not null)
        {
            product.StockQuantity += item.Quantity;
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

app.Run();

static IResult? ValidateItems(IReadOnlyList<CartItemDto> items)
{
    if (items.Count == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["At least one item is required."] });
    }

    if (items.Any(item => item.Quantity <= 0))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["quantity"] = ["Quantities must be greater than zero."] });
    }

    return null;
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}

public sealed class Product
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int StockQuantity { get; set; }

    public ProductDto ToDto() => new(Id, Sku, Name, Description, Category, UnitPrice, StockQuantity);
}

public static class CatalogSeed
{
    public static readonly Guid LaptopProId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid LowStockMonitorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static void Seed(CatalogDbContext db)
    {
        if (db.Products.Any())
        {
            return;
        }

        db.Products.AddRange(
            new Product { Id = LaptopProId, Sku = "LAP-ULTRA-14", Name = "Aurora Ultrabook 14", Description = "Lightweight business laptop with 16 GB RAM and 512 GB SSD.", Category = "Laptops", UnitPrice = 24999.00m, StockQuantity = 8 },
            new Product { Id = Guid.Parse("11111111-1111-1111-1111-111111111112"), Sku = "LAP-CREATOR-16", Name = "Sierra Creator 16", Description = "High-performance laptop for design and development workloads.", Category = "Laptops", UnitPrice = 38999.00m, StockQuantity = 5 },
            new Product { Id = Guid.Parse("11111111-1111-1111-1111-111111111113"), Sku = "LAP-STUDENT-13", Name = "Norte Student 13", Description = "Compact everyday laptop with all-day battery life.", Category = "Laptops", UnitPrice = 13999.00m, StockQuantity = 12 },
            new Product { Id = Guid.Parse("11111111-1111-1111-1111-111111111114"), Sku = "LAP-WORK-15", Name = "Puebla Workstation 15", Description = "Portable workstation with dedicated graphics.", Category = "Laptops", UnitPrice = 45999.00m, StockQuantity = 3 },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222221"), Sku = "ACC-MOUSE-WL", Name = "Wireless Precision Mouse", Description = "Ergonomic Bluetooth mouse with silent clicks.", Category = "Accessories", UnitPrice = 899.00m, StockQuantity = 25 },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Sku = "ACC-KEY-MECH", Name = "Mechanical Keyboard MX", Description = "Spanish layout mechanical keyboard with white backlight.", Category = "Accessories", UnitPrice = 2199.00m, StockQuantity = 14 },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222223"), Sku = "ACC-DOCK-USBC", Name = "USB-C Travel Dock", Description = "HDMI, Ethernet and USB-A expansion for laptops.", Category = "Accessories", UnitPrice = 1699.00m, StockQuantity = 10 },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222224"), Sku = "ACC-BAG-15", Name = "Commuter Laptop Backpack", Description = "Water-resistant backpack for laptops up to 15 inches.", Category = "Accessories", UnitPrice = 1299.00m, StockQuantity = 18 },
            new Product { Id = LowStockMonitorId, Sku = "MON-27-QHD", Name = "Vista 27 QHD Monitor", Description = "27-inch QHD IPS monitor with USB-C input.", Category = "Monitors", UnitPrice = 7999.00m, StockQuantity = 2 },
            new Product { Id = Guid.Parse("33333333-3333-3333-3333-333333333334"), Sku = "MON-32-4K", Name = "Vista 32 4K Monitor", Description = "32-inch 4K monitor for detailed creative work.", Category = "Monitors", UnitPrice = 12999.00m, StockQuantity = 6 },
            new Product { Id = Guid.Parse("33333333-3333-3333-3333-333333333335"), Sku = "MON-24-FHD", Name = "Claro 24 FHD Monitor", Description = "24-inch full HD display for office setups.", Category = "Monitors", UnitPrice = 3999.00m, StockQuantity = 16 },
            new Product { Id = Guid.Parse("33333333-3333-3333-3333-333333333336"), Sku = "MON-34-WIDE", Name = "Panorama 34 Ultrawide", Description = "Curved ultrawide monitor for multitasking.", Category = "Monitors", UnitPrice = 14999.00m, StockQuantity = 4 });
        db.SaveChanges();
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            logger.LogInformation("Handling {Method} {Path} with correlation id {CorrelationId}", context.Request.Method, context.Request.Path, correlationId);
            await next(context);
        }
    }
}
