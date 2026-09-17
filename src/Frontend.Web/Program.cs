using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddProblemDetails();
// The cart is kept in in-memory session state (AddDistributedMemoryCache stores
// session data in this process's memory, not in an external cache). This is why
// docker-compose.yml pins the frontend service to a single replica: running more
// than one instance would split cart state across containers. Horizontal scaling
// of this service requires swapping in a real distributed cache/session store,
// which is out of scope for the Docker execution-mode issue.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".StorefrontPoC.Cart";
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddHttpClient<CatalogApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:CatalogApi"] ?? StorefrontDefaults.CatalogApiUrl);
}).AddHttpMessageHandler<CorrelationIdHandler>();
builder.Services.AddHttpClient<OrdersApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:OrdersApi"] ?? StorefrontDefaults.OrdersApiUrl);
}).AddHttpMessageHandler<CorrelationIdHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));
app.MapRazorPages();

app.Run();

public sealed class CatalogApiClient(HttpClient httpClient)
{
    public async Task<ApiResult<IReadOnlyList<ProductDto>>> GetProductsAsync(string? category = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(category) ? "/api/products" : $"/api/products?category={Uri.EscapeDataString(category)}";
        return await SendAsync<IReadOnlyList<ProductDto>>(() => httpClient.GetAsync(path, cancellationToken), cancellationToken);
    }

    public async Task<ApiResult<ProductDto>> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendAsync<ProductDto>(() => httpClient.GetAsync($"/api/products/{id}", cancellationToken), cancellationToken);
    }

    private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return value is null ? ApiResult<T>.Failure("The catalog returned an empty response.", response.StatusCode) : ApiResult<T>.Success(value);
            }

            return ApiResult<T>.Failure(await ApiProblemReader.ReadMessageAsync(response, cancellationToken), response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult<T>.Failure("Catalog.Api is unavailable. Start the Catalog.Api project and try again.", HttpStatusCode.ServiceUnavailable);
        }
    }
}

public sealed class OrdersApiClient(HttpClient httpClient)
{
    public async Task<ApiResult<OrderDto>> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/api/orders", request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var order = await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken);
                return order is null ? ApiResult<OrderDto>.Failure("Orders.Api returned an empty response.", response.StatusCode) : ApiResult<OrderDto>.Success(order);
            }

            return ApiResult<OrderDto>.Failure(await ApiProblemReader.ReadMessageAsync(response, cancellationToken), response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult<OrderDto>.Failure("Orders.Api is unavailable. Start the Orders.Api project and try again.", HttpStatusCode.ServiceUnavailable);
        }
    }

    public async Task<ApiResult<OrderDto>> GetOrderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendGetAsync<OrderDto>($"/api/orders/{id}", cancellationToken);
    }

    public async Task<ApiResult<IReadOnlyList<OrderDto>>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        return await SendGetAsync<IReadOnlyList<OrderDto>>("/api/orders", cancellationToken);
    }

    private async Task<ApiResult<T>> SendGetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return value is null ? ApiResult<T>.Failure("Orders.Api returned an empty response.", response.StatusCode) : ApiResult<T>.Success(value);
            }

            return ApiResult<T>.Failure(await ApiProblemReader.ReadMessageAsync(response, cancellationToken), response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult<T>.Failure("Orders.Api is unavailable. Start the Orders.Api project and try again.", HttpStatusCode.ServiceUnavailable);
        }
    }
}

public sealed record ApiResult<T>(T? Value, string? ErrorMessage, HttpStatusCode? StatusCode)
{
    public bool IsSuccess => ErrorMessage is null;
    public static ApiResult<T> Success(T value) => new(value, null, null);
    public static ApiResult<T> Failure(string message, HttpStatusCode statusCode) => new(default, message, statusCode);
}

public static class CartSessionExtensions
{
    private const string CartKey = "cart";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static List<CartItemDto> GetCart(this ISession session)
    {
        var json = session.GetString(CartKey);
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<CartItemDto>>(json, JsonOptions) ?? [];
    }

    public static void SaveCart(this ISession session, IReadOnlyList<CartItemDto> items)
    {
        session.SetString(CartKey, JsonSerializer.Serialize(items, JsonOptions));
    }

    public static void ClearCart(this ISession session) => session.Remove(CartKey);
}

public static class ApiProblemReader
{
    public static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"The API returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var messages = new List<string>();
            if (root.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                messages.Add(detail.GetString()!);
            }

            if (root.TryGetProperty("shortages", out var shortages))
            {
                foreach (var shortage in shortages.EnumerateArray())
                {
                    var name = shortage.TryGetProperty("productName", out var productName) ? productName.GetString() : "this product";
                    var available = shortage.TryGetProperty("availableQuantity", out var availableQuantity) ? availableQuantity.GetInt32() : 0;
                    messages.Add($"Only {available} units of {name} left.");
                }
            }

            return messages.Count > 0 ? string.Join(" ", messages.Distinct()) : content;
        }
        catch (JsonException)
        {
            return content;
        }
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
