FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV TZ=Europe/Berlin
# The aspnet:10.0 base image sets ASPNETCORE_HTTP_PORTS=8080 itself, which then competes
# with the ASPNETCORE_URLS=http://+:8080 every deploy target (docker-compose.yml, k8s/01-
# config-and-secret.yaml) sets explicitly - functionally identical, but ASP.NET Core logs a
# "Overriding HTTP_PORTS... binding to values defined by URLS instead" warning on every
# startup about the redundancy. Clearing it here (not touching the deliberately-set
# ASPNETCORE_URLS convention) removes the competing source instead of the established one.
ENV ASPNETCORE_HTTP_PORTS=
# curl isn't included in the minimal aspnet runtime image, but docker-compose.yml's
# healthcheck relies on it to probe the running container from the inside. espeak-ng
# (+ its libespeak-ng.so.1) is StudyLife.Tts's phonemizer for the "read note aloud"
# feature - resolved dynamically at runtime via NativeLibrary.SetDllImportResolver,
# not linked at build time, so no -dev package is needed here.
RUN apt-get update && apt-get install -y --no-install-recommends curl espeak-ng && rm -rf /var/lib/apt/lists/*

FROM base AS final
# TARGETARCH is set automatically by "docker buildx build --platform ..." (amd64/arm64) -
# publish/{amd64,arm64} are architecture-specific outputs from the CI publish job, so each
# image variant gets binaries matching its own architecture instead of a shared RID-agnostic
# (or worse, wrong-RID) build.
ARG TARGETARCH
COPY publish/${TARGETARCH} .
# Don't run as root - .NET images have shipped their own "app" user (UID 1654) since .NET 8;
# a custom user would only collide with the Ubuntu Noble base image's GIDs (1000 is
# already taken there). chown BEFORE the VOLUME declaration so the ownership is baked into the
# image layer: a freshly created named volume (docker-compose.yml: studylife-data) picks up
# both content AND ownership from the image on its first mount. Watch out for EXISTING deployments:
# a volume already populated with root-owned files keeps that ownership - there, a one-time
#   docker run --rm -v studylife-data:/d alpine chown -R 1654:1654 /d
# is needed, otherwise the app user can no longer write to the SQLite DB.
RUN mkdir -p /app/app_data && chown -R app:app /app/app_data
VOLUME ["/app/app_data"]
USER app
ENTRYPOINT ["dotnet", "StudyLife.Server.dll"]
