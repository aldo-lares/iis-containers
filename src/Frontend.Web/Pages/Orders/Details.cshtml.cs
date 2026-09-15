using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Contracts;

namespace Frontend.Web.Pages.Orders;

public sealed class DetailsModel(OrdersApiClient ordersApi) : PageModel
{
    public OrderDto? Order { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        var result = await ordersApi.GetOrderAsync(id);
        if (result.IsSuccess)
        {
            Order = result.Value;
        }
        else
        {
            ErrorMessage = result.ErrorMessage;
        }
    }
}
