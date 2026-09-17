# Running StorefrontPoC with Docker

This document describes the Docker execution mode for StorefrontPoC. It is one of several
execution modes under evaluation for this repository (Docker, Podman, a local Kubernetes
cluster, and AKS). Nothing here designates Docker as the recommended or default tooling —
it is simply one supported way to run the PoC end to end. The local F5 mode described in the
main [README.md](../README.md) continues to work unchanged and is not affected by this mode.

The `Containerfile` in each project folder is a plain OCI multi-stage build with no
Docker-proprietary (BuildKit-only) syntax, so it can be built with either `docker build` or
`podman build`. A `Dockerfile` symlink sits next to each `Containerfile` so both
tools find their conventional filename.

## Prerequisites

- Docker Engine with the Compose plugin (`docker compose version`), or an equivalent
  OCI-compatible tool.
- No .NET SDK installation is required on the host; the SDK image is used only during the
  build stage inside the container.

## Build

From the repository root:

```bash
docker compose build
```

This builds three images (`frontend`, `catalog-api`, `orders-api`), each using the
repository root as build context so the shared `Shared.Contracts` project can be copied in.

## Run

```bash
docker compose up --build
```

This starts three containers on a shared bridge network (`storefront-net`) and waits for
`catalog-api` and `orders-api` to report healthy before starting `frontend`.

### Port map

| Service | Host port | Container port | Purpose |
| --- | --- | --- | --- |
| frontend | `5000` | `8080` | Razor Pages storefront, cart, checkout, recent orders |
| catalog-api | `5001` | `8080` | Product catalog, stock reserve/release, Swagger |
| orders-api | `5002` | `8080` | Order placement, checkout orchestration, Swagger |

Browse `http://localhost:5000` for the storefront. `ASPNETCORE_ENVIRONMENT=Development` is
set for all three services so Swagger UI stays reachable at `/swagger` on `catalog-api` and
`orders-api`.

Service-to-service calls use the compose DNS service names (`http://catalog-api:8080` and
`http://orders-api:8080`), injected as `Services__CatalogApi` / `Services__OrdersApi`
environment variables — the same configuration keys used by local F5 mode, just pointed at
container addresses instead of `localhost`.

## Data persistence

Each API stores its SQLite database file on a dedicated named volume
(`catalog-data`, `orders-data`) mounted at a writable path inside the container. Data
survives `docker compose restart` and `docker compose down` (without `-v`).

To reset the data to a clean seeded state:

```bash
docker compose down -v
docker compose up --build
```

## Health checks

All three services expose `GET /healthz`. Compose health checks call it every 5 seconds
(`curl -f http://localhost:8080/healthz` from inside the container). `frontend` declares
`depends_on: catalog-api, orders-api` with `condition: service_healthy`, so it only starts
once both APIs report healthy.

## Smoke test

With the stack running, execute the existing smoke test unchanged against the mapped host
ports (its defaults already match the port map above):

```bash
./tests/smoke/smoke-test.sh
```

## Logs

```bash
docker compose logs -f
docker compose logs -f catalog-api   # a single service
```

## Stop and clean up

```bash
docker compose down       # stop and remove containers, keep data volumes
docker compose down -v    # also remove the named data volumes
```

## Known constraint: single frontend replica

The frontend keeps the shopping cart in in-memory session state
(`AddDistributedMemoryCache` in `src/Frontend.Web/Program.cs`). `docker-compose.yml`
therefore pins `frontend` to a single replica. Running more than one frontend instance would
split cart state across containers and break checkout for users whose requests land on a
different replica. Horizontal scaling of the frontend requires introducing a real
distributed cache/session store (for example Redis), which is out of scope for this issue.
