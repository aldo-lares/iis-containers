param(
    [string]$FrontendUrl = "http://localhost:5000",
    [string]$CatalogUrl = "http://localhost:5001",
    [string]$OrdersUrl = "http://localhost:5002"
)

$ErrorActionPreference = "Stop"
$TaxRate = 0.16
$Tolerance = 0.01

function Fail([string]$Message) {
    Write-Error "Smoke test failed: $Message"
    exit 1
}

function Join-Url([string]$BaseUrl, [string]$Path) {
    return "$($BaseUrl.TrimEnd('/'))/$($Path.TrimStart('/'))"
}

function Assert-Status([string]$Url, [int]$ExpectedStatusCode) {
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -SkipHttpErrorCheck
    }
    catch {
        Fail "Request failed for $Url. $($_.Exception.Message)"
    }

    if ($response.StatusCode -ne $ExpectedStatusCode) {
        Fail "$Url returned HTTP $($response.StatusCode), expected $ExpectedStatusCode"
    }
}

Assert-Status (Join-Url $FrontendUrl "/healthz") 200
Assert-Status (Join-Url $CatalogUrl "/healthz") 200
Assert-Status (Join-Url $OrdersUrl "/healthz") 200

try {
    $products = Invoke-RestMethod -Uri (Join-Url $CatalogUrl "/api/products") -Method Get
}
catch {
    Fail "GET $CatalogUrl/api/products failed. $($_.Exception.Message)"
}

if ($products.Count -lt 12) {
    Fail "Expected at least 12 products, found $($products.Count)"
}

$product = $products | Where-Object { $_.stockQuantity -ge 2 } | Select-Object -First 1
if ($null -eq $product) {
    Fail "No product has at least 2 units in stock"
}

$beforeStock = [int]$product.stockQuantity
$orderRequest = @{
    customerName = "Smoke Test"
    customerEmail = "smoke@example.com"
    items = @(
        @{
            productId = $product.id
            quantity = 2
        }
    )
}

try {
    $orderResponse = Invoke-WebRequest -Uri (Join-Url $OrdersUrl "/api/orders") -Method Post -ContentType "application/json" -Body ($orderRequest | ConvertTo-Json -Depth 5) -SkipHttpErrorCheck
}
catch {
    Fail "POST $OrdersUrl/api/orders failed. $($_.Exception.Message)"
}

if ($orderResponse.StatusCode -ne 201) {
    Fail "Expected order POST to return 201, got $($orderResponse.StatusCode): $($orderResponse.Content)"
}

$order = $orderResponse.Content | ConvertFrom-Json
$expectedSubtotal = [decimal]$product.unitPrice * 2
$expectedTax = [Math]::Round($expectedSubtotal * $TaxRate, 2, [MidpointRounding]::AwayFromZero)
$expectedTotal = $expectedSubtotal + $expectedTax

if ([Math]::Abs([decimal]$order.subtotal - $expectedSubtotal) -ge $Tolerance) {
    Fail "Order subtotal $($order.subtotal) did not equal unit price $($product.unitPrice) times 2"
}

if ([Math]::Abs([decimal]$order.tax - $expectedTax) -ge $Tolerance) {
    Fail "Order tax $($order.tax) did not equal 16% of subtotal $expectedSubtotal"
}

if ([Math]::Abs([decimal]$order.total - ([decimal]$order.subtotal + [decimal]$order.tax)) -ge $Tolerance -or [Math]::Abs([decimal]$order.total - $expectedTotal) -ge $Tolerance) {
    Fail "Order total $($order.total) did not equal subtotal plus 16% tax"
}

$afterProduct = Invoke-RestMethod -Uri (Join-Url $CatalogUrl "/api/products/$($product.id)") -Method Get
$afterStock = [int]$afterProduct.stockQuantity
if ($afterStock -ne ($beforeStock - 2)) {
    Fail "Expected stock to decrease from $beforeStock to $($beforeStock - 2), got $afterStock"
}

$conflictRequest = @{
    customerName = "Smoke Test"
    customerEmail = "smoke@example.com"
    items = @(
        @{
            productId = $product.id
            quantity = 9999
        }
    )
}

try {
    $conflictResponse = Invoke-WebRequest -Uri (Join-Url $OrdersUrl "/api/orders") -Method Post -ContentType "application/json" -Body ($conflictRequest | ConvertTo-Json -Depth 5) -SkipHttpErrorCheck
}
catch {
    Fail "Oversized order request failed. $($_.Exception.Message)"
}

if ($conflictResponse.StatusCode -ne 409) {
    Fail "Expected oversized order to return 409, got $($conflictResponse.StatusCode)"
}

$unchangedProduct = Invoke-RestMethod -Uri (Join-Url $CatalogUrl "/api/products/$($product.id)") -Method Get
$unchangedStock = [int]$unchangedProduct.stockQuantity
if ($unchangedStock -ne $afterStock) {
    Fail "Expected stock to remain $afterStock after conflict, got $unchangedStock"
}

Write-Host "Smoke test passed. Product $($product.id) stock changed $beforeStock -> $afterStock and stayed $unchangedStock after 409."
exit 0
