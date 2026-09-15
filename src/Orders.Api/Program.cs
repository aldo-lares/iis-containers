using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy.WithOrigins("http://localhost:5000").AllowAnyHeader().AllowAnyMethod());
});

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorefrontPoC", "Orders.Api", "orders.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<OrdersDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddHttpClient<ICatalogClient, CatalogApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:CatalogApi"] ?? "http://localhost:5001");
}).AddHttpMessageHandler<CorrelationIdHandler>();

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
    await Results.Problem(title: "Unexpected error", detail: "Orders.Api could not process the request.", statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
}));

app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("Frontend");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    db.Database.EnsureCreated();
    OrdersSeed.Seed(db);
}

app.MapGet("/healthz", async (ICatalogClient catalogClient) =>
{
    var catalogHealthy = await catalogClient.PingAsync();
    return catalogHealthy ? Results.Ok(new { status = "Healthy" }) : Results.Problem(title: "Unhealthy", detail: "Catalog.Api is unreachable.", statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/orders", async (OrderRequest request, CheckoutService checkout) =>
{
    try
    {
        var result = await checkout.PlaceOrderAsync(request);
        return result.Status switch
        {
            PlaceOrderStatus.Placed => Results.Created($"/api/orders/{result.Order!.Id}", result.Order),
            PlaceOrderStatus.InvalidRequest => Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [result.Message ?? "The order request is invalid."] }),
            PlaceOrderStatus.InsufficientStock => Results.Problem(
                title: "Insufficient stock",
                detail: result.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["shortages"] = result.Shortages }),
            PlaceOrderStatus.CatalogUnavailable => Results.Problem(
                title: "Catalog unavailable",
                detail: result.Message ?? "Catalog.Api is unavailable; no order was created.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
    catch (OrderPersistenceException ex)
    {
        return Results.Problem(title: "Order persistence failed", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/orders/{id:guid}", async (Guid id, IOrderRepository repository) =>
{
    var order = await repository.GetAsync(id);
    return order is null ? Results.NotFound() : Results.Ok(order.ToDto());
});

app.MapGet("/api/orders", async (IOrderRepository repository) => Results.Ok((await repository.GetRecentAsync()).Select(order => order.ToDto())));

app.Run();

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.OrderNumber).IsRequired();
            entity.Property(order => order.CustomerName).IsRequired();
            entity.Property(order => order.CustomerEmail).IsRequired();
            entity.Property(order => order.Subtotal).HasColumnType("decimal(18,2)");
            entity.Property(order => order.Tax).HasColumnType("decimal(18,2)");
            entity.Property(order => order.Total).HasColumnType("decimal(18,2)");
            entity.HasMany(order => order.Lines).WithOne().HasForeignKey(line => line.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.Property(line => line.Sku).IsRequired();
            entity.Property(line => line.ProductName).IsRequired();
            entity.Property(line => line.UnitPrice).HasColumnType("decimal(18,2)");
            entity.Property(line => line.LineTotal).HasColumnType("decimal(18,2)");
        });
    }
}

public sealed class Order
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public List<OrderLine> Lines { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "Placed";
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OrderDto ToDto() => new(Id, OrderNumber, CustomerName, CustomerEmail, Lines.OrderBy(line => line.ProductName).Select(line => line.ToDto()).ToList(), Subtotal, Tax, Total, Status, CreatedAtUtc);
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }

    public OrderLineDto ToDto() => new(ProductId, Sku, ProductName, UnitPrice, Quantity, LineTotal);
}

public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken cancellationToken = default);
    Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Order>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public sealed class EfOrderRepository(OrdersDbContext db) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Orders.Include(order => order.Lines).FirstOrDefaultAsync(order => order.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Order>> GetRecentAsync(CancellationToken cancellationToken = default) =>
        await db.Orders.Include(order => order.Lines).OrderByDescending(order => order.CreatedAtUtc).Take(25).ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken = default) => db.Orders.CountAsync(cancellationToken);
}

public interface ICatalogClient
{
    Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<ReserveStockResult> ReserveStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default);
    Task ReleaseStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default);
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

public sealed class CatalogApiClient(HttpClient httpClient, ILogger<CatalogApiClient> logger) : ICatalogClient
{
    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/products/{productId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProductDto>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CatalogUnavailableException("Catalog.Api is unreachable while resolving products.", ex);
        }
    }

    public async Task<ReserveStockResult> ReserveStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("/api/stock/reserve", new StockReservationRequest(items), cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var shortages = await ReadShortagesAsync(response, cancellationToken);
                return ReserveStockResult.Conflict(shortages);
            }

            response.EnsureSuccessStatusCode();
            return ReserveStockResult.Success();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CatalogUnavailableException("Catalog.Api is unreachable while reserving stock.", ex);
        }
    }

    public async Task ReleaseStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/api/stock/release", new StockReleaseRequest(items), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Failed to release stock after order persistence failed");
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("/healthz", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<StockShortageDto>> ReadShortagesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("shortages", out var shortagesElement))
        {
            return [];
        }

        return shortagesElement.Deserialize<List<StockShortageDto>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }
}

public sealed class CheckoutService(ICatalogClient catalogClient, IOrderRepository repository, ILogger<CheckoutService> logger)
{
    private const decimal TaxRate = 0.16m;

    public async Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            return PlaceOrderResult.Invalid("Cart must contain at least one item.");
        }

        if (request.Items.Any(item => item.Quantity <= 0))
        {
            return PlaceOrderResult.Invalid("Quantities must be greater than zero.");
        }

        try
        {
            var items = request.Items.GroupBy(item => item.ProductId).Select(group => new CartItemDto(group.Key, group.Sum(item => item.Quantity))).ToList();
            var products = new List<ProductDto>();
            foreach (var item in items)
            {
                var product = await catalogClient.GetProductAsync(item.ProductId, cancellationToken);
                if (product is null)
                {
                    return PlaceOrderResult.Invalid($"Product {item.ProductId} was not found.");
                }

                products.Add(product);
            }

            var reservation = await catalogClient.ReserveStockAsync(items, cancellationToken);
            if (!reservation.WasReserved)
            {
                var detail = reservation.Shortages.Count == 0
                    ? "One or more products do not have enough stock."
                    : string.Join(" ", reservation.Shortages.Select(shortage => $"Only {shortage.AvailableQuantity} units of {shortage.ProductName} left."));
                return PlaceOrderResult.InsufficientStock(reservation.Shortages, detail);
            }

            var order = await BuildOrderAsync(request, items, products, cancellationToken);
            try
            {
                await repository.AddAsync(order, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Persisting order failed after stock reservation; releasing stock");
                await catalogClient.ReleaseStockAsync(items, cancellationToken);
                throw new OrderPersistenceException("The order could not be saved after stock was reserved; reserved stock was released.", ex);
            }

            return PlaceOrderResult.Placed(order.ToDto());
        }
        catch (CatalogUnavailableException ex)
        {
            logger.LogWarning(ex, "Checkout failed because Catalog.Api is unavailable");
            return PlaceOrderResult.CatalogUnavailable(ex.Message);
        }
    }

    private async Task<Order> BuildOrderAsync(OrderRequest request, IReadOnlyList<CartItemDto> items, IReadOnlyList<ProductDto> products, CancellationToken cancellationToken)
    {
        var sequence = await repository.CountAsync(cancellationToken) + 1;
        var lines = items.Select(item =>
        {
            var product = products.Single(product => product.Id == item.ProductId);
            return new OrderLine
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                Sku = product.Sku,
                ProductName = product.Name,
                UnitPrice = product.UnitPrice,
                Quantity = item.Quantity,
                LineTotal = product.UnitPrice * item.Quantity
            };
        }).ToList();
        var subtotal = lines.Sum(line => line.LineTotal);
        var tax = CalculateTax(subtotal);

        return new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = $"ORD-{DateTime.UtcNow:yyyy}-{sequence:000000}",
            CustomerName = request.CustomerName.Trim(),
            CustomerEmail = request.CustomerEmail.Trim(),
            Lines = lines,
            Subtotal = subtotal,
            Tax = tax,
            Total = subtotal + tax,
            Status = "Placed",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static decimal CalculateTax(decimal subtotal) => Math.Round(subtotal * TaxRate, 2, MidpointRounding.AwayFromZero);
}

public sealed record ReserveStockResult(bool WasReserved, IReadOnlyList<StockShortageDto> Shortages)
{
    public static ReserveStockResult Success() => new(true, []);
    public static ReserveStockResult Conflict(IReadOnlyList<StockShortageDto> shortages) => new(false, shortages);
}

public enum PlaceOrderStatus
{
    Placed,
    InvalidRequest,
    InsufficientStock,
    CatalogUnavailable
}

public sealed record PlaceOrderResult(PlaceOrderStatus Status, OrderDto? Order = null, IReadOnlyList<StockShortageDto>? Shortages = null, string? Message = null)
{
    public static PlaceOrderResult Placed(OrderDto order) => new(PlaceOrderStatus.Placed, Order: order);
    public static PlaceOrderResult Invalid(string message) => new(PlaceOrderStatus.InvalidRequest, Message: message);
    public static PlaceOrderResult InsufficientStock(IReadOnlyList<StockShortageDto> shortages, string message) => new(PlaceOrderStatus.InsufficientStock, Shortages: shortages, Message: message);
    public static PlaceOrderResult CatalogUnavailable(string message) => new(PlaceOrderStatus.CatalogUnavailable, Message: message);
}

public sealed class CatalogUnavailableException(string message, Exception innerException) : Exception(message, innerException);

public sealed class OrderPersistenceException(string message, Exception innerException) : Exception(message, innerException);

public static class OrdersSeed
{
    public static void Seed(OrdersDbContext db)
    {
        if (db.Orders.Any())
        {
            return;
        }

        db.Orders.AddRange(
            SampleOrder("ORD-2026-000001", "María García", "maria@example.com", -5),
            SampleOrder("ORD-2026-000002", "Carlos Rivera", "carlos@example.com", -2));
        db.SaveChanges();
    }

    private static Order SampleOrder(string number, string name, string email, int daysAgo)
    {
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Sku = "LAP-ULTRA-14",
            ProductName = "Aurora Ultrabook 14",
            UnitPrice = 24999.00m,
            Quantity = 1,
            LineTotal = 24999.00m
        };
        var tax = CheckoutService.CalculateTax(line.LineTotal);
        return new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = number,
            CustomerName = name,
            CustomerEmail = email,
            Lines = [line],
            Subtotal = line.LineTotal,
            Tax = tax,
            Total = line.LineTotal + tax,
            Status = "Placed",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(daysAgo)
        };
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

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            logger.LogInformation("Handling request with correlation id {CorrelationId}", correlationId);
            await next(context);
        }
    }
}

public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = accessor.HttpContext?.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
            ?? accessor.HttpContext?.Request.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            ?? Guid.NewGuid().ToString("N");

        request.Headers.Remove(CorrelationIdMiddleware.HeaderName);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        return base.SendAsync(request, cancellationToken);
    }
}
