# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish .

# Environment variables (override at runtime)
ENV QUBIC_WS_URL=wss://bob.qubic.li/ws/logs
ENV DISCORD_WEBHOOK_URL=
ENV BUNDLE_JSON_URL=https://static.qubic.org/v1/general/data/bundle.json
ENV MIN_TRANSFER_AMOUNT=1000000
ENV RECONNECT_DELAY_SECONDS=5

ENTRYPOINT ["dotnet", "QubicTransferWatcher.dll"]
