# StorefrontPoC

StorefrontPoC is a .NET 8 proof-of-concept online store optimized for local developer experience. Clone the repository, open the solution in Visual Studio 2022 or VS Code, press F5/Run, and the frontend plus both backend APIs start on fixed HTTP ports with no Docker, external database, cloud resource, secrets, npm install, or manual setup.

## Execution modes

| Mode | Status | Notes |
| --- | --- | --- |
| Local | Available | Visual Studio 2022, VS Code, or `dotnet run` on fixed local HTTP ports |
| Docker | Not available yet | Planned for a later issue |
| Podman | Not available yet | Planned for a later issue |
| Local cluster | Not available yet | Planned for a later issue |
| AKS | Not available yet | Planned for a later issue |

## Architecture

```text
+----------------------+        +---------------------+        +---------------------+
| Frontend.Web         |  HTTP  | Orders.Api          |  HTTP  | Catalog.Api         |
| Razor Pages + cart   +------->| checkout orchestration+----->| products + stock    |
| Session in memory    |        | SQLite orders       |        | SQLite catalog      |
+----------------------+        +---------------------+        +---------------------+
        :5000                           :5002                          :5001
```

All service-to-service traffic uses HTTP to avoid development-certificate friction. SQLite database files are created automatically under the user's local application data folder on first run by `EnsureCreated`, and seed data is idempotent.

## Running locally

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022, or VS Code with the C# Dev Kit extension

### Port map

| Project | URL | Purpose |
| --- | --- | --- |
| Frontend.Web | `http://localhost:5000` | Razor Pages storefront, cart, checkout, recent orders |
| Catalog.Api | `http://localhost:5001` | Product catalog, stock reserve/release, Swagger in Development |
| Orders.Api | `http://localhost:5002` | Order placement, checkout orchestration, Swagger in Development |

Health endpoints are available at `/healthz` on all three projects. Swagger UI is available at `/swagger` for Catalog.Api and Orders.Api in Development.

### Run with Visual Studio 2022

1. Open `StorefrontPoC.sln`.
2. Select the `StorefrontPoC (all projects)` solution launch profile if prompted.
3. Press F5. Visual Studio starts all three projects with IIS Express.
4. Browse `http://localhost:5000` for the storefront.

### Run with VS Code

1. Open the repository folder.
2. Go to Run and Debug.
3. Select `StorefrontPoC (all projects)`.
4. Press F5. The compound launch starts Catalog.Api, Orders.Api, and Frontend.Web.

### Smoke test

Start all three projects first, then run one of:

```bash
./tests/smoke/smoke-test.sh
```

```powershell
./tests/smoke/smoke-test.ps1
```

Both scripts default to the local ports above and also accept `-FrontendUrl`, `-CatalogUrl`, and `-OrdersUrl` when testing another local endpoint set.

### Configuration overrides

Local defaults are defined in `appsettings.Development.json` and launch profiles. When needed, standard ASP.NET Core environment variables can override service URLs (`Services__CatalogApi`, `Services__OrdersApi`), SQLite database locations (`ConnectionStrings__CatalogDb`, `ConnectionStrings__OrdersDb`), and listening URLs (`ASPNETCORE_URLS`).

### Troubleshooting

If a project fails to start because a port is already in use, stop the process listening on `5000`, `5001`, or `5002`, then press F5/Run again. The local launch profiles intentionally keep fixed HTTP ports so the projects can find each other without manual setup.

## API contracts

| Service | Method | Route | Description |
| --- | --- | --- | --- |
| Catalog.Api | GET | `/api/products` | Product list; optional `?category=` filter |
| Catalog.Api | GET | `/api/products/{id}` | Single product; 404 when missing |
| Catalog.Api | POST | `/api/stock/reserve` | Atomically reserve all requested stock or return 409 with shortages |
| Catalog.Api | POST | `/api/stock/release` | Compensating stock release |
| Orders.Api | POST | `/api/orders` | Place an order from customer details and cart items |
| Orders.Api | GET | `/api/orders/{id}` | Fetch one order |
| Orders.Api | GET | `/api/orders` | Recent orders, newest first |

Checkout always resolves current product name and unit price from Catalog.Api server-side, reserves stock before creating the order, computes 16% IVA, and releases reserved stock if order persistence fails.

## Scripted end-to-end demo

1. Start all projects and open `http://localhost:5000`.
2. Browse the catalog. It is seeded with 12 products across Laptops, Accessories, and Monitors.
3. Add 2 units of a product to the cart, then open `/cart` and proceed to checkout.
4. Enter a name and email, place the order, and land on `/orders/{id}` with the generated order number and totals.
5. Verify stock changed:
   - Before checkout, call `GET http://localhost:5001/api/products/{productId}` and note `stockQuantity`.
   - After checkout, call the same URL and confirm the value dropped by exactly 2.
6. Demo insufficient stock:
   - Use the `Vista 27 QHD Monitor`, seeded with stock `2`.
   - Try to order more than 2 units.
   - The UI shows a friendly banner such as `Only 2 units of Vista 27 QHD Monitor left.`
   - Orders.Api returns 409, no order is created, and Catalog.Api stock remains unchanged.
7. Demo Catalog.Api outage:
   - Stop Catalog.Api.
   - Try to checkout from the frontend.
   - Orders.Api returns 503 with problem details, and the UI shows a friendly unavailable-service banner.
8. Demo correlation IDs:
   - Place a checkout request.
   - Inspect the three console windows.
   - The same `X-Correlation-Id` value is logged by Frontend.Web, Orders.Api, and Catalog.Api for that request chain.

## Validate

```bash
dotnet build StorefrontPoC.sln
dotnet test StorefrontPoC.sln
```
