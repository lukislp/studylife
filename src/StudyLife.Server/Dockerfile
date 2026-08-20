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
# not linked at build time, so no -dev package is needed here. libgomp1 is the OpenMP
# runtime StudyLife.Stt's bundled whisper.cpp native library needs (voice dictation,
# not yet wired into the API) - confirmed missing via a plain "dotnet <app>.dll" run
# failing with "Failed to load native whisper library", root-caused with ldd showing
# "libgomp.so.1 => not found" against the Whisper.net.Runtime package's linux-x64 .so.
# Piper voice models are ~15-70MB binary ONNX files - not checked into this repo (would
# bloat every clone/diff forever), pulled from Hugging Face at image build time instead, the
# same way the base image itself and NuGet packages are already external build-time
# dependencies. PiperVoiceRegistry expects "{lang}.onnx" + "{lang}.onnx.json" pairs here.
# Only de+en baked in (~126MB) - deliberate: 21 of the app's 26 languages have a Piper voice,
# but baking in all of them adds ~1.2GB to an image every self-hoster pulls (Pi cluster/ARM
# VPS included), a cost not everyone needs. Every other language falls back to the browser's
# own Web Speech API client-side (see index.html's speakText + Notes.razor) - full native
# coverage for the remaining 19 is a possible future addition, not required for the feature
# to work everywhere. See docs/TTS-VOICES.md for each voice's license.
RUN apt-get update && apt-get install -y --no-install-recommends curl espeak-ng libgomp1 && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/tts-voices \
    && curl -fsSL -o /app/tts-voices/de.onnx https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/low/de_DE-thorsten-low.onnx \
    && curl -fsSL -o /app/tts-voices/de.onnx.json https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/low/de_DE-thorsten-low.onnx.json \
    && curl -fsSL -o /app/tts-voices/en.onnx https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx \
    && curl -fsSL -o /app/tts-voices/en.onnx.json https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx.json

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
