# One container for the whole demo: the SPA is built, dropped into wwwroot, and served by the API
# that also answers /api. Same origin in production as in development, so there is no CORS
# configuration anywhere in the repo and nothing to get wrong on a deploy.

# --- 1. The SPA -------------------------------------------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src/Web

# Manifests first: the dependency layer is only rebuilt when the manifests change, not on every
# edit to a component.
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci

COPY src/Web/ ./
# vite.config.ts writes to ../Api/wwwroot, so the bundle lands at /src/Api/wwwroot.
RUN npm run build

# --- 2. The API -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /source

COPY src/Api/Api.csproj src/Api/
RUN dotnet restore src/Api/Api.csproj

COPY src/Api/ src/Api/
COPY --from=web /src/Api/wwwroot src/Api/wwwroot

# The Web SDK carries wwwroot into the publish output, so the bundle ships inside the app.
RUN dotnet publish src/Api/Api.csproj --configuration Release --no-restore --output /app

# --- 3. The image that runs ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is here for the container health check and nothing else; the runtime image ships without it.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=api /app ./

# The two files the app looks for beside itself: the write gate's rules and the versioned prompt.
# They are configuration, not code, so they stay readable and diffable in the image.
COPY policies.json ./
COPY prompts/ ./prompts/

# Nothing here needs to write to disk — state is in PostgreSQL — so the app runs unprivileged.
USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Api.dll"]
