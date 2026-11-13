# KnA - Konjiki no Autoencoder — headless web edition
#
#   docker build -t kna .
#   docker run -d -p 8585:8585 -v /path/to/anime:/media -v kna-config:/config kna
#
# Hardware encoding:
#   Intel / AMD (VA-API, Quick Sync):  --device /dev/dri:/dev/dri
#   NVIDIA (NVENC):                    --gpus all   (needs nvidia-container-toolkit on the host)

# ---------------------------------------------------------------- build
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
RUN dotnet publish src/KnA.Server/KnA.Server.csproj -c Release --no-restore -o /app

# ---------------------------------------------------------------- runtime
FROM ubuntu:24.04
ARG DEBIAN_FRONTEND=noninteractive
ARG TARGETARCH
# ffmpeg (libass, x264, x265, SVT-AV1, NVENC, VA-API, QSV) + GPU user-space drivers.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      aspnetcore-runtime-10.0 ca-certificates tzdata \
      ffmpeg fontconfig fonts-dejavu-core \
      mesa-va-drivers vainfo \
 && if [ "$TARGETARCH" = "amd64" ] || [ -z "$TARGETARCH" ]; then \
      apt-get install -y --no-install-recommends \
        intel-media-va-driver-non-free libvpl2 libmfx-gen1.2 ; \
    fi \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app /app

ENV KNA_DATA_DIR=/config \
    KNA_MEDIA_ROOTS=/media \
    ASPNETCORE_URLS=http://+:8585 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility

RUN mkdir -p /config /media
VOLUME ["/config"]
EXPOSE 8585
WORKDIR /app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8585 && printf 'GET /api/info HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q 200 <&3"]
ENTRYPOINT ["dotnet", "/app/kna-server.dll"]
