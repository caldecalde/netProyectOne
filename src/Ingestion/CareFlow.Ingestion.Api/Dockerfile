# Build context: repository root.
#   docker build -f src/Ingestion/CareFlow.Ingestion.Api/Dockerfile -t careflow/ingestion-api .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore in its own layer so it is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BuildingBlocks/CareFlow.BuildingBlocks/CareFlow.BuildingBlocks.csproj src/BuildingBlocks/CareFlow.BuildingBlocks/
COPY src/Ingestion/CareFlow.Ingestion.Domain/CareFlow.Ingestion.Domain.csproj src/Ingestion/CareFlow.Ingestion.Domain/
COPY src/Ingestion/CareFlow.Ingestion.Application/CareFlow.Ingestion.Application.csproj src/Ingestion/CareFlow.Ingestion.Application/
COPY src/Ingestion/CareFlow.Ingestion.Infrastructure/CareFlow.Ingestion.Infrastructure.csproj src/Ingestion/CareFlow.Ingestion.Infrastructure/
COPY src/Ingestion/CareFlow.Ingestion.Api/CareFlow.Ingestion.Api.csproj src/Ingestion/CareFlow.Ingestion.Api/
RUN dotnet restore src/Ingestion/CareFlow.Ingestion.Api/CareFlow.Ingestion.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Ingestion/ src/Ingestion/
RUN dotnet publish src/Ingestion/CareFlow.Ingestion.Api/CareFlow.Ingestion.Api.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled: no shell, no package manager, runs as non-root (uid 1654) on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CareFlow.Ingestion.Api.dll"]
