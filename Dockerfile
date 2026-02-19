# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy local NuGet packages and csproj, then restore
COPY nuget-local/ /nuget-local/
COPY src/QubicTransferWatcher/QubicTransferWatcher.csproj src/QubicTransferWatcher/
RUN dotnet restore src/QubicTransferWatcher/QubicTransferWatcher.csproj --source /nuget-local --source https://api.nuget.org/v3/index.json

# Copy everything and publish
COPY . .
RUN dotnet publish src/QubicTransferWatcher/QubicTransferWatcher.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish .

# Environment variables (override at runtime)
# Use __ separator for nested/array config, e.g. BobNodes__0, BobNodes__1
ENV BobNodes__0=https://bobnet.qubic.li
ENV DiscordWebhookUrl=
ENV BundleJsonUrl=https://static.qubic.org/v1/general/data/bundle.json
ENV MinTransferAmount=1000000
ENV MinBurnAmount=50
ENV ReconnectDelaySeconds=5

ENTRYPOINT ["dotnet", "QubicTransferWatcher.dll"]
