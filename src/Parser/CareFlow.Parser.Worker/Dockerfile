# Build context: repository root.
#   docker build -f src/Parser/CareFlow.Parser.Worker/Dockerfile -t careflow/parser-worker .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore in its own layer so it is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BuildingBlocks/CareFlow.BuildingBlocks/CareFlow.BuildingBlocks.csproj src/BuildingBlocks/CareFlow.BuildingBlocks/
COPY src/BuildingBlocks/CareFlow.BuildingBlocks.Aws/CareFlow.BuildingBlocks.Aws.csproj src/BuildingBlocks/CareFlow.BuildingBlocks.Aws/
COPY src/Parser/CareFlow.Parser.Domain/CareFlow.Parser.Domain.csproj src/Parser/CareFlow.Parser.Domain/
COPY src/Parser/CareFlow.Parser.Application/CareFlow.Parser.Application.csproj src/Parser/CareFlow.Parser.Application/
COPY src/Parser/CareFlow.Parser.Infrastructure/CareFlow.Parser.Infrastructure.csproj src/Parser/CareFlow.Parser.Infrastructure/
COPY src/Parser/CareFlow.Parser.Worker/CareFlow.Parser.Worker.csproj src/Parser/CareFlow.Parser.Worker/
RUN dotnet restore src/Parser/CareFlow.Parser.Worker/CareFlow.Parser.Worker.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Parser/ src/Parser/
RUN dotnet publish src/Parser/CareFlow.Parser.Worker/CareFlow.Parser.Worker.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled: no shell, no package manager, runs as non-root (uid 1654) on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CareFlow.Parser.Worker.dll"]
