# syntax=docker/dockerfile:1
# DomiBase API image (future). Prepared, but intentionally NOT built or published yet.
# Build context: the repository root. See infrastructure/docker/README.md.

# ---------- Build stage ----------
# Pinned .NET 10 SDK image (Ubuntu 24.04 LTS), digest captured 2026-11-17.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

# Restore first so dependency layers are cached when only source changes.
COPY backend/src/DomiBase.Api/DomiBase.Api.csproj backend/src/DomiBase.Api/
COPY backend/src/DomiBase.Domain/DomiBase.Domain.csproj backend/src/DomiBase.Domain/
COPY backend/src/DomiBase.Application/DomiBase.Application.csproj backend/src/DomiBase.Application/
COPY backend/src/DomiBase.Infrastructure/DomiBase.Infrastructure.csproj backend/src/DomiBase.Infrastructure/
COPY backend/src/DomiBase.Provisioning/DomiBase.Provisioning.csproj backend/src/DomiBase.Provisioning/
RUN dotnet restore backend/src/DomiBase.Api/DomiBase.Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/src/DomiBase.Api/DomiBase.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:UseAppHost=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

# ---------- Runtime stage ----------
# Pinned ASP.NET Core 10 runtime image (Ubuntu 24.04 LTS), digest captured 2026-11-17.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime

# curl is used only by the container HEALTHCHECK. No package index is kept.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# .NET images provide the non-root "app" user (UID 1654).
COPY --from=build --chown=app:app /app/publish ./
USER app

# Secrets and connection strings are injected at runtime; nothing is baked in.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Liveness does not depend on PostgreSQL; readiness is separate at /health/ready.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["curl", "-fsS", "http://localhost:8080/health"]

ENTRYPOINT ["dotnet", "DomiBase.Api.dll"]
