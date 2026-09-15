using Microsoft.Extensions.Logging;
using Shared.Contracts;

public sealed class CheckoutServiceTests
{
    private static readonly Guid ProductId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly ProductDto Product = new(ProductId, "SKU-1", "Test Laptop", "A test product", "Laptops", 100m, 5);

    [Fact]
    public async Task PlaceOrder_CalculatesSubtotalTaxAndTotal()
    {
        var catalog = new FakeCatalogClient { Products = { [ProductId] = Product } };
        var repository = new FakeOrderRepository();
        var service = CreateService(catalog, repository);

        var result = await service.PlaceOrderAsync(new OrderRequest("Ada", "ada@example.com", [new CartItemDto(ProductId, 2)]));

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        Assert.NotNull(result.Order);
        Assert.Equal(200m, result.Order.Subtotal);
        Assert.Equal(32m, result.Order.Tax);
        Assert.Equal(232m, result.Order.Total);
        Assert.Single(repository.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsInvalidQuantities()
    {
        var catalog = new FakeCatalogClient { Products = { [ProductId] = Product } };
        var repository = new FakeOrderRepository();
        var service = CreateService(catalog, repository);

        var result = await service.PlaceOrderAsync(new OrderRequest("Ada", "ada@example.com", [new CartItemDto(ProductId, 0)]));

        Assert.Equal(PlaceOrderStatus.InvalidRequest, result.Status);
        Assert.Empty(repository.Orders);
        Assert.False(catalog.ReserveCalled);
    }

    [Fact]
    public async Task PlaceOrder_ReturnsConflictAndCreatesNoOrderWhenStockIsInsufficient()
    {
        var shortage = new StockShortageDto(ProductId, Product.Name, 10, 2);
        var catalog = new FakeCatalogClient
        {
            Products = { [ProductId] = Product },
            ReservationResult = ReserveStockResult.Conflict([shortage])
        };
        var repository = new FakeOrderRepository();
        var service = CreateService(catalog, repository);

        var result = await service.PlaceOrderAsync(new OrderRequest("Ada", "ada@example.com", [new CartItemDto(ProductId, 10)]));

        Assert.Equal(PlaceOrderStatus.InsufficientStock, result.Status);
        Assert.Same(shortage, Assert.Single(result.Shortages!));
        Assert.Empty(repository.Orders);
        Assert.False(catalog.ReleaseCalled);
    }

    [Fact]
    public async Task PlaceOrder_ReleasesStockWhenPersistenceFailsAfterReservation()
    {
        var catalog = new FakeCatalogClient { Products = { [ProductId] = Product } };
        var repository = new FakeOrderRepository { ThrowOnAdd = true };
        var service = CreateService(catalog, repository);

        await Assert.ThrowsAsync<OrderPersistenceException>(() => service.PlaceOrderAsync(new OrderRequest("Ada", "ada@example.com", [new CartItemDto(ProductId, 1)])));

        Assert.True(catalog.ReserveCalled);
        Assert.True(catalog.ReleaseCalled);
        Assert.Equal(ProductId, Assert.Single(catalog.ReleasedItems).ProductId);
    }

    private static CheckoutService CreateService(ICatalogClient catalog, IOrderRepository repository) => new(catalog, repository, new TestLogger<CheckoutService>());

    private sealed class FakeCatalogClient : ICatalogClient
    {
        public Dictionary<Guid, ProductDto> Products { get; init; } = [];
        public ReserveStockResult ReservationResult { get; init; } = ReserveStockResult.Success();
        public bool ReserveCalled { get; private set; }
        public bool ReleaseCalled { get; private set; }
        public IReadOnlyList<CartItemDto> ReleasedItems { get; private set; } = [];

        public Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default) => Task.FromResult(Products.GetValueOrDefault(productId));

        public Task<ReserveStockResult> ReserveStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default)
        {
            ReserveCalled = true;
            return Task.FromResult(ReservationResult);
        }

        public Task ReleaseStockAsync(IReadOnlyList<CartItemDto> items, CancellationToken cancellationToken = default)
        {
            ReleaseCalled = true;
            ReleasedItems = items;
            return Task.CompletedTask;
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public List<Order> Orders { get; } = [];
        public bool ThrowOnAdd { get; init; }

        public Task AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("Simulated persistence failure");
            }

            Orders.Add(order);
            return Task.CompletedTask;
        }

        public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Orders.FirstOrDefault(order => order.Id == id));
        public Task<IReadOnlyList<Order>> GetRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>(Orders);
        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Orders.Count);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
