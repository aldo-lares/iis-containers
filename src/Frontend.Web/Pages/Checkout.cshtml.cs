using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Contracts;

namespace Frontend.Web.Pages;

public sealed class CheckoutModel(CatalogApiClient catalogApi, OrdersApiClient ordersApi) : PageModel
{
    [BindProperty] public string CustomerName { get; set; } = string.Empty;
    [BindProperty] public string CustomerEmail { get; set; } = string.Empty;
    public List<CartLineViewModel> Lines { get; private set; } = [];
    public decimal Subtotal => Lines.Sum(line => line.LineTotal);
    public decimal Tax => Math.Round(Subtotal * 0.16m, 2, MidpointRounding.AwayFromZero);
    public decimal Total => Subtotal + Tax;
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (Lines.Count == 0)
        {
            ErrorMessage = "Your cart is empty.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(CustomerName) || string.IsNullOrWhiteSpace(CustomerEmail))
        {
            ErrorMessage = "Enter your name and email to place the order.";
            return Page();
        }

        var request = new OrderRequest(CustomerName, CustomerEmail, HttpContext.Session.GetCart());
        var result = await ordersApi.PlaceOrderAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Checkout failed. No order was created.";
            return Page();
        }

        HttpContext.Session.ClearCart();
        return RedirectToPage("/Orders/Details", new { id = result.Value.Id });
    }

    private async Task LoadAsync()
    {
        Lines = [];
        foreach (var item in HttpContext.Session.GetCart())
        {
            var product = await catalogApi.GetProductAsync(item.ProductId);
            if (product.IsSuccess && product.Value is not null)
            {
                Lines.Add(new CartLineViewModel(product.Value, item.Quantity));
            }
            else
            {
                ErrorMessage = product.ErrorMessage ?? "Unable to load the checkout summary.";
            }
        }
    }
}
