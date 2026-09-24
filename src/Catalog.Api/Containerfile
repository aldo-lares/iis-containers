# syntax-agnostic, plain OCI multi-stage build (no BuildKit-only features, no --mount)
# Buildable with either `docker build` or `podman build`.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore in a separate layer for cache efficiency: only project files are copied first.
COPY ["src/Catalog.Api/Catalog.Api.csproj", "src/Catalog.Api/"]
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
RUN dotnet restore "src/Catalog.Api/Catalog.Api.csproj"

# Now copy the full source and publish.
COPY ["src/Catalog.Api/", "src/Catalog.Api/"]
COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
RUN dotnet publish "src/Catalog.Api/Catalog.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# curl is used by the compose healthcheck to probe /healthz.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Pre-create the data directory owned by the non-root user so a fresh named
# volume mounted here inherits writable permissions (Data Source for SQLite).
RUN mkdir -p /data/catalog-api && chown -R $APP_UID /data/catalog-api

# Run as the non-root user provided by the base image (rootless-compatible).
USER $APP_UID

ENTRYPOINT ["dotnet", "Catalog.Api.dll"]
