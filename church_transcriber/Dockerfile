ARG BUILD_FROM=mcr.microsoft.com/dotnet/aspnet:10.0-noble

# ---------- Build the .NET web app ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY app/ ./
RUN dotnet publish ChurchTranscriber.csproj -c Release -o /out /p:UseAppHost=false

# ---------- Runtime ----------
FROM ${BUILD_FROM}
ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    VIRTUAL_ENV=/opt/venv \
    PATH="/opt/venv/bin:/opt/deno:${PATH}" \
    HF_HOME=/data/models/hf \
    DATA_DIR=/data \
    ASPNETCORE_URLS=http://0.0.0.0:8099 \
    DOTNET_gcServer=0

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg python3 python3-venv python3-pip ca-certificates curl unzip tzdata \
 && rm -rf /var/lib/apt/lists/*

# yt-dlp (with its YouTube challenge solver) + faster-whisper for local transcription
RUN python3 -m venv /opt/venv \
 && /opt/venv/bin/pip install --no-cache-dir --upgrade pip \
 && /opt/venv/bin/pip install --no-cache-dir "yt-dlp[default]" faster-whisper

# Deno: the JavaScript runtime yt-dlp needs for YouTube downloads
RUN set -eux; \
    case "$(uname -m)" in \
      x86_64) DENO_ARCH=x86_64-unknown-linux-gnu ;; \
      aarch64) DENO_ARCH=aarch64-unknown-linux-gnu ;; \
      *) echo "unsupported arch" && exit 1 ;; \
    esac; \
    mkdir -p /opt/deno; \
    curl -fsSL -o /tmp/deno.zip "https://github.com/denoland/deno/releases/latest/download/deno-${DENO_ARCH}.zip"; \
    unzip -o /tmp/deno.zip -d /opt/deno; rm /tmp/deno.zip; /opt/deno/deno --version

WORKDIR /app
COPY --from=build /out/ ./
COPY run.sh /run.sh
RUN chmod a+x /run.sh

EXPOSE 8099
CMD ["/run.sh"]
