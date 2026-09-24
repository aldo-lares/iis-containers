# Running StorefrontPoC with Podman

This document describes the Podman execution mode for StorefrontPoC. It is one of several
execution modes under evaluation; it does not designate Podman or Docker as recommended. The
local F5 and Docker modes continue to work unchanged.

The three existing Containerfiles are plain OCI multi-stage builds. Podman consumes them
without modification. The existing `docker-compose.yml` is also shared by Docker Compose and
`podman compose`; no separate Podman compose file is needed.

## Prerequisites

- Podman with rootless storage configured (`podman info --format '{{.Host.Security.Rootless}}'`
  should print `true`).
- A Compose provider discoverable by `podman compose`, for the compose path.
- No .NET SDK installation is required on the host.

Run all commands as the regular user, without `sudo`. Ports 5000-5002 are above 1024, so
rootless Podman needs no privileged-port workaround.

## Build

### Compose images

From the repository root:

```bash
podman compose build
```

### Pod images

The native pod manifest uses explicit local image names. Build them from the repository root:

```bash
podman build -t localhost/storefrontpoc-catalog-api:latest \
  -f src/Catalog.Api/Containerfile .
podman build -t localhost/storefrontpoc-orders-api:latest \
  -f src/Orders.Api/Containerfile .
podman build -t localhost/storefrontpoc-frontend:latest \
  -f src/Frontend.Web/Containerfile .
```

## Run

### Compose

```bash
podman compose up --build
```

This uses the existing `docker-compose.yml`. Compose service-name DNS connects the containers,
and the APIs' health checks control startup ordering.

### Podman-native pod

After building the three explicitly tagged pod images:

```bash
podman play kube deploy/podman/storefront-pod.yaml
```

All containers in a pod share one network namespace. The manifest therefore assigns distinct
internal ports and configures service calls through `localhost` rather than compose DNS names.

### Port map

| Service | Host port | Compose container port | Pod container port |
| --- | --- | --- | --- |
| frontend | `5000` | `8080` | `8080` |
| catalog-api | `5001` | `8080` | `8081` |
| orders-api | `5002` | `8080` | `8082` |

Browse `http://localhost:5000` for the storefront.

## Data persistence and rootless permissions

The compose path uses two named volumes. Their mounts include `:Z`, so Podman applies a private
SELinux label on enforcing hosts. The native pod path uses one PVC-backed named volume for the
two differently named SQLite files. Podman manages its SELinux label automatically because
Kubernetes `volumeMounts` do not support a `:Z` suffix.
Both pod mounts expose that volume's root, so `catalog.db` and `orders.db` are stored side by
side; their distinct basenames also keep SQLite journal and WAL files separate.

The Containerfiles run as the non-root `$APP_UID`. For Compose, their pre-created data
directories give new named volumes the required ownership. For the pod path, PVC annotations
initialize ownership to UID/GID 1654. Consequently the supplied workflows do not require
`--userns=keep-id`.

If replacing the named volume with a custom host bind mount, ensure that directory is writable
by the mapped container user and privately SELinux-relabel it (`:Z` in compose syntax).
`podman play kube --userns=keep-id deploy/podman/storefront-pod.yaml` can preserve the invoking
user's identity for such a customized mount, but it does not replace correct directory
ownership or SELinux labeling.

## Smoke test

With either deployment running, execute the existing test unchanged. Its default URLs match
the published ports:

```bash
./tests/smoke/smoke-test.sh
```

## Logs

Compose:

```bash
podman compose logs -f
podman compose logs -f catalog-api
```

Native pod:

```bash
podman pod logs -f storefrontpoc
podman logs -f storefrontpoc-catalog-api
```

Use `podman pod ps` and `podman ps --pod` to inspect status.

## Stop and clean up

Compose:

```bash
podman compose down
podman compose down -v
```

Native pod:

```bash
podman kube down deploy/podman/storefront-pod.yaml
podman volume rm storefront-data
```

Omit the volume-removal command to retain SQLite data for the next run.

## Compatibility findings

The Containerfiles required no changes, and `docker-compose.yml` is consumed by both compose
implementations. The pod-native differences in networking, DNS, startup, SELinux handling, and
volume ownership are recorded in [deploy/podman/README.md](../deploy/podman/README.md).
