#!/usr/bin/env bash
set -u

FrontendUrl="http://localhost:5000"
CatalogUrl="http://localhost:5001"
OrdersUrl="http://localhost:5002"

fail() {
  echo "Smoke test failed: $*" >&2
  exit 1
}

usage() {
  cat >&2 <<USAGE
Usage: $0 [-FrontendUrl URL] [-CatalogUrl URL] [-OrdersUrl URL]
USAGE
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    -FrontendUrl|--frontend-url)
      [ "$#" -ge 2 ] || fail "$1 requires a value"
      FrontendUrl="$2"
      shift 2
      ;;
    -CatalogUrl|--catalog-url)
      [ "$#" -ge 2 ] || fail "$1 requires a value"
      CatalogUrl="$2"
      shift 2
      ;;
    -OrdersUrl|--orders-url)
      [ "$#" -ge 2 ] || fail "$1 requires a value"
      OrdersUrl="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "Unknown argument: $1"
      ;;
  esac
done

trim_slash() {
  printf '%s' "${1%/}"
}

FrontendUrl="$(trim_slash "$FrontendUrl")"
CatalogUrl="$(trim_slash "$CatalogUrl")"
OrdersUrl="$(trim_slash "$OrdersUrl")"

http_status() {
  curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$1"
}

assert_status() {
  local url="$1"
  local expected="$2"
  local status
  status="$(http_status "$url")" || fail "Request failed for $url"
  [ "$status" = "$expected" ] || fail "$url returned HTTP $status, expected $expected"
}

extract_json_string() {
  local name="$1"
  sed -nE "s/.*\"$name\"[[:space:]]*:[[:space:]]*\"([^\"]+)\".*/\1/p"
}

extract_json_number() {
  local name="$1"
  sed -nE "s/.*\"$name\"[[:space:]]*:[[:space:]]*([0-9]+(\.[0-9]+)?).*/\1/p"
}

assert_status "$FrontendUrl/healthz" "200"
assert_status "$CatalogUrl/healthz" "200"
assert_status "$OrdersUrl/healthz" "200"

products_json="$(curl --silent --show-error --fail "$CatalogUrl/api/products")" || fail "GET $CatalogUrl/api/products failed"
product_count="$(printf '%s' "$products_json" | grep -o '"id"' | wc -l | tr -d ' ')"
[ "$product_count" -ge 12 ] || fail "Expected at least 12 products, found $product_count"

product_line="$(printf '%s' "$products_json" | sed 's/},{/}\n{/g' | awk -F'"stockQuantity":' 'NF > 1 { split($2, a, /[^0-9]/); if (a[1] >= 2) { print; exit } }')"
[ -n "$product_line" ] || fail "No product has at least 2 units in stock"

product_id="$(printf '%s' "$product_line" | extract_json_string id)"
unit_price="$(printf '%s' "$product_line" | extract_json_number unitPrice)"
before_stock="$(printf '%s' "$product_line" | extract_json_number stockQuantity)"
[ -n "$product_id" ] || fail "Could not parse product id"
[ -n "$unit_price" ] || fail "Could not parse product unit price"
[ -n "$before_stock" ] || fail "Could not parse product stock"

order_body="{\"customerName\":\"Smoke Test\",\"customerEmail\":\"smoke@example.com\",\"items\":[{\"productId\":\"$product_id\",\"quantity\":2}]}"
order_response_file="$(mktemp)"
order_status="$(curl --silent --show-error --output "$order_response_file" --write-out '%{http_code}' -H 'Content-Type: application/json' --data "$order_body" "$OrdersUrl/api/orders")" || { rm -f "$order_response_file"; fail "POST $OrdersUrl/api/orders failed"; }
[ "$order_status" = "201" ] || { response="$(cat "$order_response_file")"; rm -f "$order_response_file"; fail "Expected order POST to return 201, got $order_status: $response"; }
order_json="$(cat "$order_response_file")"
rm -f "$order_response_file"

subtotal="$(printf '%s' "$order_json" | extract_json_number subtotal)"
tax="$(printf '%s' "$order_json" | extract_json_number tax)"
total="$(printf '%s' "$order_json" | extract_json_number total)"
[ -n "$subtotal" ] || fail "Could not parse order subtotal"
[ -n "$tax" ] || fail "Could not parse order tax"
[ -n "$total" ] || fail "Could not parse order total"

awk -v subtotal="$subtotal" -v tax="$tax" -v total="$total" 'BEGIN { expected = subtotal + tax; diff = total - expected; if (diff < 0) diff = -diff; exit(diff < 0.01 ? 0 : 1) }' \
  || fail "Order total $total did not equal subtotal $subtotal plus tax $tax"
awk -v subtotal="$subtotal" -v unitPrice="$unit_price" 'BEGIN { expected = unitPrice * 2; diff = subtotal - expected; if (diff < 0) diff = -diff; exit(diff < 0.01 ? 0 : 1) }' \
  || fail "Order subtotal $subtotal did not equal unit price $unit_price times 2"
awk -v subtotal="$subtotal" -v tax="$tax" 'BEGIN { expected = sprintf("%.2f", subtotal * 0.16); diff = tax - expected; if (diff < 0) diff = -diff; exit(diff < 0.01 ? 0 : 1) }' \
  || fail "Order tax $tax did not equal 16% of subtotal $subtotal"

after_product_json="$(curl --silent --show-error --fail "$CatalogUrl/api/products/$product_id")" || fail "GET $CatalogUrl/api/products/$product_id failed after order"
after_stock="$(printf '%s' "$after_product_json" | extract_json_number stockQuantity)"
[ "$after_stock" -eq $((before_stock - 2)) ] || fail "Expected stock to decrease from $before_stock to $((before_stock - 2)), got $after_stock"

conflict_body="{\"customerName\":\"Smoke Test\",\"customerEmail\":\"smoke@example.com\",\"items\":[{\"productId\":\"$product_id\",\"quantity\":9999}]}"
conflict_response_file="$(mktemp)"
conflict_status="$(curl --silent --show-error --output "$conflict_response_file" --write-out '%{http_code}' -H 'Content-Type: application/json' --data "$conflict_body" "$OrdersUrl/api/orders")" || { rm -f "$conflict_response_file"; fail "Conflict order request failed"; }
rm -f "$conflict_response_file"
[ "$conflict_status" = "409" ] || fail "Expected oversized order to return 409, got $conflict_status"

unchanged_product_json="$(curl --silent --show-error --fail "$CatalogUrl/api/products/$product_id")" || fail "GET $CatalogUrl/api/products/$product_id failed after conflict"
unchanged_stock="$(printf '%s' "$unchanged_product_json" | extract_json_number stockQuantity)"
[ "$unchanged_stock" -eq "$after_stock" ] || fail "Expected stock to remain $after_stock after conflict, got $unchanged_stock"

echo "Smoke test passed. Product $product_id stock changed $before_stock -> $after_stock and stayed $unchanged_stock after 409."
