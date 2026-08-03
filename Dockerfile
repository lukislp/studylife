# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy project files first (for layer caching) - only the three that the server publish
# actually needs via ProjectReference (Client + Shared); the test projects are deliberately excluded.
COPY src/StudyLife.Client/StudyLife.Client.csproj src/StudyLife.Client/
COPY src/StudyLife.Server/StudyLife.Server.csproj src/StudyLife.Server/
COPY src/StudyLife.Shared/StudyLife.Shared.csproj src/StudyLife.Shared/

# Restore dependencies (targeting the server project specifically instead of the whole .sln, which
# also lists the test projects whose csproj files haven't even been copied here yet)
RUN dotnet restore src/StudyLife.Server/StudyLife.Server.csproj

# Copy the rest of the source
COPY . .

# Build the server (which includes the client WASM)
RUN dotnet publish src/StudyLife.Server/StudyLife.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Without this the container runs on UTC instead of Berlin local time - the whole app treats
# DateTime.Now as "floating" local wall-clock time (see docs/ARCHITECTURE.md); session reminders &
# co. would otherwise fire systematically shifted by the UTC offset. Must match
# src/StudyLife.Server/Dockerfile (the production path), which already sets the same TZ.
ENV TZ=Europe/Berlin

# Install curl for healthcheck
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Non-root user for security (UID 1000 is already taken in the aspnet:10.0 base image)
RUN useradd -m -u 1001 appuser && chown -R appuser:appuser /app
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "StudyLife.Server.dll"]
