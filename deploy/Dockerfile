# syntax=docker/dockerfile:1
# Production image: Angular SPA + ASP.NET Core API/BFF on one origin (design §10.1).
# Build from the repository root:  docker build -f deploy/Dockerfile -t subscribed:local .
# Base images are pinned by version tag. Pin by digest in your registry mirror for production.

# --- 1. SPA (contract: `npm run build` → frontend/dist/subscribed/browser) -------------------
FROM node:24.22.2-bookworm-slim AS spa
WORKDIR /src/frontend
ENV CI=true NG_CLI_ANALYTICS=false
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build && test -f dist/subscribed/browser/index.html

# --- 2. API -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS api
WORKDIR /src/backend
COPY backend/Directory.Build.props backend/Directory.Build.targets backend/Directory.Packages.props backend/.editorconfig ./
COPY backend/src/Subscribed.Platform/Subscribed.Platform.csproj backend/src/Subscribed.Platform/packages.lock.json src/Subscribed.Platform/
COPY backend/src/Subscribed.Household/Subscribed.Household.csproj backend/src/Subscribed.Household/packages.lock.json src/Subscribed.Household/
COPY backend/src/Subscribed.Subscriptions/Subscribed.Subscriptions.csproj backend/src/Subscribed.Subscriptions/packages.lock.json src/Subscribed.Subscriptions/
COPY backend/src/Subscribed.Api/Subscribed.Api.csproj backend/src/Subscribed.Api/packages.lock.json src/Subscribed.Api/
RUN dotnet restore src/Subscribed.Api/Subscribed.Api.csproj --locked-mode
COPY backend/src/ ./src/
RUN dotnet publish src/Subscribed.Api/Subscribed.Api.csproj -c Release -o /app --no-restore -p:ContinuousIntegrationBuild=true \
 && rm -rf /app/wwwroot

# --- 3. Runtime: non-root, no SDK, no secrets ---------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS runtime
WORKDIR /app
COPY --from=api /app ./
COPY --from=spa /src/frontend/dist/subscribed/browser ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
# APP_UID (1654) is the non-root user predefined by the official .NET images.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "/app/Subscribed.Api.dll", "--probe-live"]
ENTRYPOINT ["dotnet", "/app/Subscribed.Api.dll"]
