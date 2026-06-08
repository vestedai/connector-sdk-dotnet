# Build context: vested-ai-sdks/dotnet/
# docker build -f examples/crm-connector/Dockerfile -t crm-connector .

# ── Stage 1: build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the entire dotnet sub-tree so the ProjectReference to the SDK resolves.
COPY . .

RUN dotnet publish examples/crm-connector/CrmConnector.csproj \
    -c Release \
    -o /app \
    --no-self-contained

# ── Stage 2: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# Run as non-root ($APP_UID is set by the base image, defaults to 1654).
USER $APP_UID

ENTRYPOINT ["dotnet", "CrmConnector.dll"]
