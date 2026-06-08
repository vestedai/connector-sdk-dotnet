# The SDK itself is a library (published to NuGet as VestedAI.ConnectorSdk).
# This image ships the CRM example as the runnable reference connector:
# customers can run it to see a working connector, or use it as a base
# image and swap in their own published connector DLL.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish examples/crm-connector/CrmConnector.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "CrmConnector.dll"]
