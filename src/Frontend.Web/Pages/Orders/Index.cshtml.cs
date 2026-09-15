using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Contracts;

namespace Frontend.Web.Pages.Orders;

public sealed class IndexModel(OrdersApiClient ordersApi) : PageModel
{
    public IReadOnlyList<OrderDto> Orders { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        var result = await ordersApi.GetOrdersAsync();
        if (result.IsSuccess)
        {
            Orders = result.Value!;
        }
        else
        {
            ErrorMessage = result.ErrorMessage;
        }
    }
}
