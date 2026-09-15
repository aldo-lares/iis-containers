using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Contracts;

namespace Frontend.Web.Pages;

public sealed class IndexModel(CatalogApiClient catalogApi) : PageModel
{
    public IReadOnlyList<ProductDto> Products { get; private set; } = [];
    public IReadOnlyList<string> Categories { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [TempData] public string? BannerMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var allProducts = await catalogApi.GetProductsAsync();
        if (!allProducts.IsSuccess)
        {
            ErrorMessage = allProducts.ErrorMessage;
            return;
        }

        Categories = allProducts.Value!.Select(product => product.Category).Distinct().Order().ToList();
        Products = string.IsNullOrWhiteSpace(Category)
            ? allProducts.Value!
            : allProducts.Value!.Where(product => product.Category == Category).ToList();
    }

    public IActionResult OnPostAdd(Guid productId, int quantity, string? category)
    {
        if (quantity <= 0)
        {
            ErrorMessage = "Choose a quantity greater than zero.";
            return RedirectToPage(new { category });
        }

        var cart = HttpContext.Session.GetCart();
        var existing = cart.FirstOrDefault(item => item.ProductId == productId);
        if (existing is null)
        {
            cart.Add(new CartItemDto(productId, quantity));
        }
        else
        {
            cart[cart.IndexOf(existing)] = existing with { Quantity = existing.Quantity + quantity };
        }

        HttpContext.Session.SaveCart(cart);
        BannerMessage = "Item added to cart.";
        return RedirectToPage(new { category });
    }
}
