# KnA - Konjiki no Autoencoder — headless web edition
#
#   docker compose up -d --build          (see docker-compose.yml: bind mounts only, no volumes)
#   docker build -t kna .
#   docker run -d -p 8585:8585 \
#     --mount type=bind,src=/srv/kna/config,dst=/config \
#     --mount type=bind,src=/mnt/anime,dst=/media \
#     kna
#
# Hardware encoding:
#   Intel / AMD (VA-API, Quick Sync, Vulkan):  --device /dev/dri:/dev/dri
#   NVIDIA (NVENC, CUDA for the AI upscaler):  --gpus all   (needs nvidia-container-toolkit on the host)
#
# Build arguments:
#   FFMPEG=btbn-release   newest BtbN release branch, static (default)
#   FFMPEG=btbn-master    BtbN nightly master
#   FFMPEG=distro         Ubuntu's FFmpeg package
#   FDKAAC=1              build the standalone fdkaac encoder (best AAC) into the image (default 1)

# ---------------------------------------------------------------- KnA (.NET)
FROM ubuntu:24.04 AS build
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends dotnet-sdk-10.0 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Directory.Build.props ./
COPY src/KnA.Core/KnA.Core.csproj src/KnA.Core/
COPY src/KnA.Server/KnA.Server.csproj src/KnA.Server/
RUN dotnet restore src/KnA.Server/KnA.Server.csproj
COPY src/KnA.Core/ src/KnA.Core/
COPY src/KnA.Server/ src/KnA.Server/
COPY ai/ ai/
RUN dotnet publish src/KnA.Server/KnA.Server.csproj -c Release --no-restore -o /app

# ---------------------------------------------------------------- fdkaac (Fraunhofer AAC, standalone program)
FROM ubuntu:24.04 AS fdkaac
ARG DEBIAN_FRONTEND=noninteractive
ARG FDKAAC=1
COPY build/build-fdkaac.sh /build-fdkaac.sh
RUN set -e; mkdir -p /out; \
    if [ "$FDKAAC" = "1" ]; then \
      apt-get update && apt-get install -y --no-install-recommends git cmake make g++ ca-certificates; \
      case "$(uname -m)" in aarch64) target=linux-arm64 ;; *) target=linux-x64 ;; esac; \
      bash /build-fdkaac.sh "$target" /out; \
    fi

# ---------------------------------------------------------------- FFmpeg (newest static build by default)
FROM ubuntu:24.04 AS ffmpeg
ARG DEBIAN_FRONTEND=noninteractive
ARG FFMPEG=btbn-release
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl xz-utils python3 && rm -rf /var/lib/apt/lists/*
RUN set -e; mkdir -p /out; if [ "$FFMPEG" != "distro" ]; then \
      t=linux64; [ "$TARGETARCH" = "arm64" ] && t=linuxarm64; \
      if [ "$FFMPEG" = "btbn-master" ]; then asset="ffmpeg-master-latest-$t-gpl.tar.xz"; \
      else \
        # newest "ffmpeg-nX.Y-latest-<target>-gpl-X.Y.tar.xz" listed in the "latest" release
        asset="$( (curl -fsSL https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest || echo '{"assets":[]}') \
          | python3 -c "import json,re,sys; t='$t'; n=[a['name'] for a in json.load(sys.stdin)['assets']]; \
r=[(tuple(map(int,m.group(1,2))),x) for x in n for m in [re.match(r'ffmpeg-n(\d+)\.(\d+)-latest-'+t+r'-gpl-\1\.\2\.tar\.xz$',x)] if m]; \
print(max(r)[1] if r else 'ffmpeg-master-latest-'+t+'-gpl.tar.xz')")"; \
      fi; \
      echo "FFmpeg: $asset"; \
      curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset" -o /tmp/ff.tar.xz \
      && tar -xJf /tmp/ff.tar.xz -C /tmp && cp /tmp/ffmpeg-*/bin/ffmpeg /tmp/ffmpeg-*/bin/ffprobe /out/ && rm -rf /tmp/ff*; \
    fi

# ---------------------------------------------------------------- runtime
FROM ubuntu:24.04
ARG DEBIAN_FRONTEND=noninteractive
ARG TARGETARCH
ARG FFMPEG=btbn-release
# GPU user-space drivers (VA-API, QSV, Vulkan incl. the lavapipe fallback), fonts for libass, Python for the AI runtime.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      aspnetcore-runtime-10.0 ca-certificates tzdata fontconfig fonts-dejavu-core fonts-noto-cjk \
      mesa-va-drivers vainfo libvulkan1 mesa-vulkan-drivers \
      python3 python3-venv \
 && if [ "$FFMPEG" = "distro" ]; then apt-get install -y --no-install-recommends ffmpeg; fi \
 && if [ "$TARGETARCH" = "amd64" ] || [ -z "$TARGETARCH" ]; then \
      apt-get install -y --no-install-recommends intel-media-va-driver-non-free libvpl2 libmfx-gen1.2 ; \
    fi \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app /app
COPY --from=ffmpeg /out/ /usr/local/bin/
COPY --from=fdkaac /out/ /app/tools/

ENV KNA_DATA_DIR=/config \
    KNA_MEDIA_ROOTS=/media \
    KNA_UPLOAD_DIR=/media/uploads \
    ASPNETCORE_URLS=http://+:8585 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility,graphics

# No VOLUME: every folder is a bind mount chosen in docker-compose.yml / docker run.
RUN mkdir -p /config /media
EXPOSE 8585
WORKDIR /app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8585 && printf 'GET /api/info HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q 200 <&3"]
ENTRYPOINT ["dotnet", "/app/kna-server.dll"]
