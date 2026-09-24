# Podman deployment notes

This directory contains the Podman-native pod definition. These are factual compatibility
notes for comparison with Docker; neither tool is designated as preferred.

## Containerfiles and Compose

The three existing Containerfiles build without Podman-specific changes. They remain the same
plain OCI image definitions used by Docker.

`podman compose` accepts the existing root-level `docker-compose.yml`, so there is no separate
`podman-compose.yml`. Compose provides service-name DNS (`catalog-api` and `orders-api`) on its
bridge network in the same way used by the Docker workflow. The named-volume mounts carry the
`:Z` private SELinux relabel option, which is accepted by both tools and matters on
SELinux-enforcing hosts.

## Pod networking and DNS

`podman play kube` places all three containers in one pod and therefore one network namespace.
They use `localhost`, not compose DNS names, for inter-container calls. To prevent bind
collisions, frontend, Catalog.Api, and Orders.Api listen inside the pod on ports 8080, 8081,
and 8082 respectively. Host ports remain 5000, 5001, and 5002.

## Persistent volume permissions

The Kubernetes YAML uses one Podman-managed persistent volume claim for both distinct SQLite
files. PVC annotations initialize the volume for the image's non-root `$APP_UID` (1654).
Podman manages the named volume's SELinux label; Kubernetes `volumeMounts` have no `:Z` suffix
syntax.

The supplied named-volume workflow does not require `--userns=keep-id`. That option can be
useful for a custom host bind mount whose files must retain the invoking user's UID, but such
a bind mount must also be privately relabeled (the equivalent of `:Z`) on an SELinux host.

## Compose behavior

Compose health-based `depends_on` ordering is not represented in the pod YAML. The three
processes start together, and service calls occur only after requests arrive. Health endpoints
remain available on every published host port.
