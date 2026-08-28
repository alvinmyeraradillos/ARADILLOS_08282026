# Build from the repository ROOT, not from this directory:
#   docker build -f src/FileProcessing.Api/Dockerfile -t fileprocessing-api .
#
# The context is the repository root because the build needs Directory.Build.props and the two
# project references alongside the API project.

# ---------------------------------------------------------------------------------------------
# Restore stage. Project files only, so the restore layer is cached until a dependency actually
# changes — editing a .cs file must not force every package to be downloaded again.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /source

COPY Directory.Build.props global.json ./
COPY src/FileProcessing.Core/FileProcessing.Core.csproj src/FileProcessing.Core/
COPY src/FileProcessing.Infrastructure/FileProcessing.Infrastructure.csproj src/FileProcessing.Infrastructure/
COPY src/FileProcessing.Api/FileProcessing.Api.csproj src/FileProcessing.Api/

RUN dotnet restore src/FileProcessing.Api/FileProcessing.Api.csproj

# ---------------------------------------------------------------------------------------------
# Build and publish.
# ---------------------------------------------------------------------------------------------
FROM restore AS publish
ARG BUILD_CONFIGURATION=Release

COPY src/ src/

RUN dotnet publish src/FileProcessing.Api/FileProcessing.Api.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app

# ---------------------------------------------------------------------------------------------
# Runtime. Only the published output is carried forward: no SDK, no source, no NuGet cache.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="File Processing API" \
      org.opencontainers.image.description="Secure file processing service with tracking and reporting" \
      org.opencontainers.image.source="https://github.com/alvinmyeraradillos/ARADILLOS_08282026"

WORKDIR /app
COPY --from=publish /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    # No diagnostic IPC socket in a deployed container; it is an attack surface that only a
    # developer attaching a profiler would ever want.
    DOTNET_EnableDiagnostics=0

# The base image ships a non-root "app" user. Running as root in a service whose whole job is
# parsing files from outside the trust boundary is an unnecessary risk.
USER app

EXPOSE 8080

# No HEALTHCHECK directive on purpose. The runtime image deliberately carries no shell utilities
# or network tools — adding curl just to poll a port would hand an attacker the exact tool they
# want. The service exposes /health/live and /health/ready over HTTP instead, which is what an
# orchestrator (Kubernetes probes, an ALB target group, compose) should call from outside.
ENTRYPOINT ["dotnet", "FileProcessing.Api.dll"]
