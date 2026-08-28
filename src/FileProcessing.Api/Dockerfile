# Build from the repository root:  docker build -f src/FileProcessing.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy project files first so restore is cached independently of source changes.
COPY Directory.Build.props global.json ./
COPY src/FileProcessing.Core/*.csproj src/FileProcessing.Core/
COPY src/FileProcessing.Infrastructure/*.csproj src/FileProcessing.Infrastructure/
COPY src/FileProcessing.Api/*.csproj src/FileProcessing.Api/
RUN dotnet restore src/FileProcessing.Api/FileProcessing.Api.csproj

COPY src/ src/
RUN dotnet publish src/FileProcessing.Api/FileProcessing.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The base image ships a non-root "app" user; running as root in a container that
# parses untrusted files is an unnecessary risk.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FileProcessing.Api.dll"]
