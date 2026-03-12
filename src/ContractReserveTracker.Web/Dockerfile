FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy local NuGet packages and project files
COPY ["nuget-local/", "/nuget-local/"]
COPY ["src/ContractReserveTracker.Web/ContractReserveTracker.Web.csproj", "src/ContractReserveTracker.Web/"]
COPY ["src/ContractReserveTracker.Shared/ContractReserveTracker.Shared.csproj", "src/ContractReserveTracker.Shared/"]

# Restore dependencies
RUN dotnet restore "src/ContractReserveTracker.Web/ContractReserveTracker.Web.csproj" --source /nuget-local --source https://api.nuget.org/v3/index.json

# Copy source code
COPY ["src/ContractReserveTracker.Web/", "src/ContractReserveTracker.Web/"]
COPY ["src/ContractReserveTracker.Shared/", "src/ContractReserveTracker.Shared/"]

# Build
WORKDIR "/src/src/ContractReserveTracker.Web"
RUN dotnet build "ContractReserveTracker.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ContractReserveTracker.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create data directory for SQLite
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "ContractReserveTracker.Web.dll"]
