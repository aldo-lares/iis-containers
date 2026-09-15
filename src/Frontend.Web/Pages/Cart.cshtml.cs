using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Contracts;

namespace Frontend.Web.Pages;

public sealed class CartModel(CatalogApiClient catalogApi) : PageModel
{
    public List<CartLineViewModel> Lines { get; private set; } = [];
    public decimal Subtotal => Lines.Sum(line => line.LineTotal);
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public IActionResult OnPostUpdate(Guid productId, int quantity)
    {
        var cart = HttpContext.Session.GetCart();
        var existing = cart.FirstOrDefault(item => item.ProductId == productId);
        if (existing is not null)
        {
            if (quantity <= 0)
            {
                cart.Remove(existing);
            }
            else
            {
                cart[cart.IndexOf(existing)] = existing with { Quantity = quantity };
            }
        }

        HttpContext.Session.SaveCart(cart);
        return RedirectToPage();
    }

    public IActionResult OnPostRemove(Guid productId)
    {
        var cart = HttpContext.Session.GetCart();
        cart.RemoveAll(item => item.ProductId == productId);
        HttpContext.Session.SaveCart(cart);
        return RedirectToPage();
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
                ErrorMessage = product.ErrorMessage ?? "Unable to load one or more cart items.";
            }
        }
    }
}

public sealed record CartLineViewModel(ProductDto Product, int Quantity)
{
    public decimal LineTotal => Product.UnitPrice * Quantity;
}
