namespace Shared.Contracts;

public sealed record ProductDto(
    Guid Id,
    string Sku,
    string Name,
    string Description,
    string Category,
    decimal UnitPrice,
    int StockQuantity);

public sealed record CartItemDto(Guid ProductId, int Quantity);

public sealed record StockReservationRequest(IReadOnlyList<CartItemDto> Items);

public sealed record StockReleaseRequest(IReadOnlyList<CartItemDto> Items);

public sealed record StockShortageDto(Guid ProductId, string ProductName, int RequestedQuantity, int AvailableQuantity);

public sealed record StockReservationConflictResponse(IReadOnlyList<StockShortageDto> Shortages);

public sealed record OrderRequest(string CustomerName, string CustomerEmail, IReadOnlyList<CartItemDto> Items);

public sealed record OrderLineDto(
    Guid ProductId,
    string Sku,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<OrderLineDto> Lines,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateOrderResponse(Guid Id, string OrderNumber, decimal Total);
