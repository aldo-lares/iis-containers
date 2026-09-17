# syntax-agnostic, plain OCI multi-stage build (no BuildKit-only features, no --mount)
# Buildable with either `docker build` or `podman build`.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore in a separate layer for cache efficiency: only project files are copied first.
COPY ["src/Frontend.Web/Frontend.Web.csproj", "src/Frontend.Web/"]
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
RUN dotnet restore "src/Frontend.Web/Frontend.Web.csproj"

# Now copy the full source and publish.
COPY ["src/Frontend.Web/", "src/Frontend.Web/"]
COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
RUN dotnet publish "src/Frontend.Web/Frontend.Web.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# curl is used by the compose healthcheck to probe /healthz.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Run as the non-root user provided by the base image (rootless-compatible).
USER $APP_UID

ENTRYPOINT ["dotnet", "Frontend.Web.dll"]
